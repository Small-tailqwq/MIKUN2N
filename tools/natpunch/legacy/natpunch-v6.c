/*
 * natpunch v4 — incrementing symmetric<->symmetric UDP hole punch.
 *
 * Implements the classic "measure current port, then immediately spray the
 * peer's forward port range in order" method (per: A New Method for Symmetric
 * NAT Traversal). The decisive detail our earlier version got wrong: the
 * measurement must NOT pollute the counter between measuring and spraying, or
 * our real send ports drift away from what we told the peer and every reply is
 * dropped by our port-restricted filter. So: measure with a MINIMAL burst,
 * report both current bank tops, and on a server-synchronized GO both sides
 * spray each other's [bank .. bank+N] in increasing order at the same instant,
 * so index i on one side lands on the peer's i-th mapping with a matching
 * restriction.
 *
 * Usage:  natpunch.exe client <server_ip> <room>
 * Build:  gcc -O2 -o natpunch.exe natpunch.c -lws2_32
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <winsock2.h>
#include <ws2tcpip.h>

#define PORT_A 21001

#define NPROBE   8      /* probes to catch both current-counter banks */
/* window for the peer's STABLE punch-socket port (tight; cone port is fixed) */
#define SP_LO   -4
#define SP_HI   30
/* window for the peer's CURRENT-COUNTER banks (wide; symmetric per-dest ports
 * sit here and climb during a spray, so cover a generous forward range) */
#define CB_LO   -8
#define CB_HI   200

static void send_text(SOCKET s, const char *t, const char *ip, int port) {
    struct sockaddr_in a; memset(&a,0,sizeof(a));
    a.sin_family=AF_INET; a.sin_port=htons((unsigned short)port); a.sin_addr.s_addr=inet_addr(ip);
    sendto(s,t,(int)strlen(t),0,(struct sockaddr*)&a,sizeof(a));
}
static void send_addr(SOCKET s, const char *t, struct sockaddr_in *a) {
    sendto(s,t,(int)strlen(t),0,(struct sockaddr*)a,sizeof(*a));
}
static int recv_to(SOCKET s, char *buf, int buflen, struct sockaddr_in *from, int ms) {
    fd_set rf; struct timeval tv; FD_ZERO(&rf); FD_SET(s,&rf);
    tv.tv_sec=ms/1000; tv.tv_usec=(ms%1000)*1000;
    if(select(0,&rf,NULL,NULL,&tv)<=0) return -1;
    int fl=sizeof(*from); int n=recvfrom(s,buf,buflen-1,0,(struct sockaddr*)from,&fl);
    if(n<0) return -1; buf[n]=0; return n;
}
static SOCKET make_udp(void){
    SOCKET s=socket(AF_INET,SOCK_DGRAM,IPPROTO_UDP);
    struct sockaddr_in a; memset(&a,0,sizeof(a));
    a.sin_family=AF_INET; a.sin_addr.s_addr=htonl(INADDR_ANY); a.sin_port=0;
    bind(s,(struct sockaddr*)&a,sizeof(a)); return s;
}
static int probe_once(const char *server_ip){
    SOCKET s=make_udp(); int ext=-1; struct sockaddr_in from; char buf[256];
    send_text(s,"PROBE",server_ip,PORT_A);
    if(recv_to(s,buf,sizeof(buf),&from,1000)>0 && !strncmp(buf,"PROBED",6)){
        char ip[64],lbl[32]; sscanf(buf,"PROBED %63s %d %31s",ip,&ext,lbl);
    }
    closesocket(s); return ext;
}
/* measure two current bank tops with a minimal probe burst */
static int measure_banks(const char *server_ip,int *top1,int *top2){
    int p[NPROBE],n=0,i,j;
    for(i=0;i<NPROBE;i++){ int v=probe_once(server_ip); if(v>0) p[n++]=v; }
    if(n<1) return 0;
    for(i=0;i<n;i++) for(j=i+1;j<n;j++) if(p[j]<p[i]){int t=p[i];p[i]=p[j];p[j]=t;}
    int b1max=-1,b1cnt=0,b2max=-1,b2cnt=0,cs=0;
    for(i=1;i<=n;i++){
        if(i==n || p[i]-p[i-1]>200){
            int cnt=i-cs,mx=p[i-1];
            if(cnt>b1cnt){b2max=b1max;b2cnt=b1cnt;b1max=mx;b1cnt=cnt;}
            else if(cnt>b2cnt){b2max=mx;b2cnt=cnt;}
            cs=i;
        }
    }
    *top1=b1max; *top2=(b2max>0)?b2max:b1max;
    return 1;
}

static void spray_range(SOCKET s,const char*pm,const char*peer_ip,int base,int lo,int hi){
    int off; if(base<=0) return;
    for(off=lo;off<=hi;off++){ int q=base+off; if(q>=1&&q<=65535) send_text(s,pm,peer_ip,q); }
}
/* spray the peer's two current-counter banks FIRST (the symmetric case, and the
 * common one), so these packets get the LOW source-port indices that fall
 * inside the peer's own coverage window; the peer's stable punch port (only
 * useful if it is a cone) is sprayed LAST so it doesn't push the important
 * counter-window sources out of range. */
static void spray_peer(SOCKET s,const char*peer_ip,const char*room,int psp,int pcb1,int pcb2){
    char pm[96]; sprintf(pm,"PUNCH %s",room);
    spray_range(s,pm,peer_ip,pcb1,CB_LO,CB_HI);
    if(pcb2!=pcb1) spray_range(s,pm,peer_ip,pcb2,CB_LO,CB_HI);
    spray_range(s,pm,peer_ip,psp,SP_LO,SP_HI);
}

static int hole_punch(const char *server_ip,const char *room){
    SOCKET s=make_udp();
    printf("=== natpunch v6 (双上报: 固定端口 + 当前计数器, 锥形/对称通吃) ===\n");
    printf("[打洞] 房间='%s', 等待配对与 GO...\n",room);

    int my_sp=-1, my_cb1=-1, my_cb2=-1;               /* our stable port + current banks */
    char peer_ip[64]=""; int p_sp=-1,p_cb1=-1,p_cb2=-1;
    char real_ip[64]=""; int real_port=-1; struct sockaddr_in real_addr; int have_real=0;
    int success=0; time_t success_t=0;
    time_t t0=time(NULL),last_join=0,last_banks=0,last_go_spray=0;
    int rounds=0;
    char buf[2048]; struct sockaddr_in from;

    while(time(NULL)-t0<300){
        time_t now=time(NULL);
        if(success && now-success_t>3) break;

        if(peer_ip[0]==0 && now-last_join>=2){
            char j[96]; sprintf(j,"JOIN %s",room); send_text(s,j,server_ip,PORT_A); last_join=now;
        }
        /* Every 2s: (1) measure current-counter banks with throwaway sockets;
         * (2) probe the server FROM the punch socket s to learn s's own stable
         * port. The PROBED reply triggers the 3-value BANKS report. */
        if(now-last_banks>=2){
            measure_banks(server_ip,&my_cb1,&my_cb2);
            send_text(s,"PROBE",server_ip,PORT_A);
            last_banks=now;
        }
        /* keep hammering a learned real endpoint regardless of GO */
        if(have_real){
            char pm[96],pk[96]; sprintf(pm,"PUNCH %s",room); sprintf(pk,"PACK %s",room);
            int k; for(k=0;k<6;k++){ send_addr(s,pm,&real_addr); send_addr(s,pk,&real_addr); }
        }

        int n=recv_to(s,buf,sizeof(buf),&from,200);
        if(n<=0) continue;
        char sip[64]; strcpy(sip,inet_ntoa(from.sin_addr)); int sport=ntohs(from.sin_port);

        if(!strncmp(buf,"PROBED",6)){
            char ip[64],lbl[32]; int mp=-1; sscanf(buf,"PROBED %63s %d %31s",ip,&mp,lbl);
            if(mp>0){ my_sp=mp;
                char b[128]; sprintf(b,"BANKS %s %d %d %d",room,my_sp,my_cb1,my_cb2);
                send_text(s,b,server_ip,PORT_A); }
        } else if(!strncmp(buf,"PEER ",5) && peer_ip[0]==0){
            char pip[64]; int pp; if(sscanf(buf,"PEER %63s %d",pip,&pp)==2){ strcpy(peer_ip,pip);
                printf("[打洞] 配对成功, 对方 %s\n",peer_ip); }
        } else if(!strncmp(buf,"PEERBANKS ",10)){
            char pip[64]; int a,b,c; if(sscanf(buf,"PEERBANKS %63s %d %d %d",pip,&a,&b,&c)==4){
                if(peer_ip[0]==0) strcpy(peer_ip,pip); p_sp=a; p_cb1=b; p_cb2=c; }
        } else if(!strncmp(buf,"GO",2)){
            if(peer_ip[0] && (p_sp>0||p_cb1>0) && now!=last_go_spray){
                spray_peer(s,peer_ip,room,p_sp,p_cb1,p_cb2);
                last_go_spray=now; rounds++;
                printf("[打洞] GO#%d 喷 peer 固定=%d 计数器banks=[%d,%d]\n",rounds,p_sp,p_cb1,p_cb2);
            }
        } else if(!strncmp(buf,"PUNCH",5)){
            if(!have_real) printf("[打洞] ← 收到对方 PUNCH! 来自 %s:%d (我方过滤放行了)\n",sip,sport);
            strcpy(real_ip,sip); real_port=sport; real_addr=from; have_real=1;
            char pk[96]; sprintf(pk,"PACK %s",room); int k; for(k=0;k<4;k++) send_addr(s,pk,&from);
        } else if(!strncmp(buf,"PACK",4)){
            strcpy(real_ip,sip); real_port=sport; real_addr=from; have_real=1;
            if(!success){ success=1; success_t=now; printf("[打洞] ★ 双向打通! 对方真实端点 %s:%d\n",sip,sport); }
        }
    }

    printf("\n============================================\n");
    if(success)        printf("结果: 打洞成功 [OK]  对方真实端点 %s:%d\n",real_ip,real_port);
    else if(have_real) printf("结果: 单向可达 [!]  收到过对方包 %s:%d 但没完成双向握手\n",real_ip,real_port);
    else               printf("结果: 未打通 [X]  没收到对方任何包 (共 %d 轮 GO)\n",rounds);
    printf("============================================\n把整段发给房主。\n");
    closesocket(s); return success;
}

int main(int argc,char**argv){
    setvbuf(stdout, NULL, _IONBF, 0);   /* unbuffered so redirected logs are live */
    WSADATA w; WSAStartup(MAKEWORD(2,2),&w);
    if(argc>=4 && !strcmp(argv[1],"client")) hole_punch(argv[2],argv[3]);
    else printf("用法: natpunch.exe client <服务器IP> <房间号>\n");
    printf("\n(按回车退出)\n"); getchar(); WSACleanup(); return 0;
}

