"""Deterministic local checks; no sockets, actual interfaces or server access."""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
from unittest.mock import patch

sys.dont_write_bytecode = True
repo = Path(__file__).resolve().parents[2]

def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, repo / 'supernode' / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

traffic = load('traffic', 'relay-traffic.py')
receiver = load('receiver', 'diagnostics-receiver.py')
root = Path(sys.argv[1]).resolve()
root.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(dir=root) as temporary:
    dbfile = Path(temporary) / 'traffic.sqlite3'
    db = traffic.open_db(dbfile)
    counters = {'rx': 1000, 'tx': 2000, 'boot': 'boot-one'}
    class FakePath:
        def __init__(self, value): self.value = str(value)
        def __truediv__(self, other): return FakePath(self.value + '/' + other)
        def read_text(self):
            return {'/proc/net/route': 'header\neth0 00000000 00000001 0003\n',
                    '/proc/sys/kernel/random/boot_id': counters['boot'],
                    '/sys/class/net/eth0/ifindex': '2',
                    '/sys/class/net/eth0/statistics/rx_bytes': str(counters['rx']),
                    '/sys/class/net/eth0/statistics/tx_bytes': str(counters['tx'])}[self.value]
    with patch.object(traffic, 'Path', FakePath), patch.object(traffic.time, 'time', return_value=7200):
        traffic.collect_host(db)
        assert db.execute('SELECT COUNT(*) FROM host_hourly').fetchone()[0] == 0
        counters.update(rx=1500, tx=2900)
        traffic.collect_host(db)
        traffic.collect_host(db)
        assert db.execute('SELECT rx,tx FROM host_hourly').fetchone() == (500,900)
        counters.update(rx=100,tx=150,boot='boot-two')
        traffic.collect_host(db)
        assert db.execute('SELECT rx,tx FROM host_hourly').fetchone() == (500,900)
    db.execute('INSERT INTO host_hourly VALUES (?,?,?,?)',(10800,'eth0',999,999))
    db.execute('INSERT INTO hourly VALUES (?,?,?,?,?,?,?)',(7200,'','','',0,700,10))
    db.commit(); db.close()
    output = io.StringIO()
    with contextlib.redirect_stdout(output):
        traffic.report(SimpleNamespace(db=str(dbfile), since=7200,until=10800,hours=1,json=True))
    data=json.loads(output.getvalue())
    assert data['host'][0]['tx_bytes']==900 and data['flows'][0]['bytes']==700
    assert traffic.hour_timestamp('2026-09-17T14:00:00+08:00')==traffic.hour_timestamp('2026-09-17T06:00:00Z')
    print('PASS: host baseline, deltas, duplicate sampling, reboot, aligned report boundaries')

    storage=receiver.Storage(str(Path(temporary)/'logs'),'offline')
    def row(session):
        return (json.dumps(dict(schema=1,batch='offline',session=session))+'\n').encode()
    size=len(row('a'))
    with patch.object(receiver,'MAX_TOTAL',size*2), patch.object(receiver,'MAX_SESSION',size*2):
        assert storage.store('offline','a','00000000',row('a'))==201
        assert storage.store('offline','a','00000000',row('a'))==200
        assert storage.total==size
        assert storage.store('offline','a','00000002',row('a'))==409
        assert storage.store('offline','b','00000000',row('b'))==201
        os.utime(storage.root/'offline/a/received.json',(1,1))
        assert storage.store('offline','c','00000000',row('c'))==201
        assert not (storage.root/'offline/a').exists() and storage.total==size*2
        assert storage.store('offline','a','00000000',row('a'))==410
        os.utime(storage.root/'offline/b/received.json',(1,1)); storage.prune()
        assert not (storage.root/'offline/b').exists()
        assert storage.store('offline','b','00000000',row('b'))==410
    print('PASS: receiver idempotence, sequence checks, oldest-segment eviction and retention')

    community_list=Path(temporary)/'community.list'
    community_list.write_text('# comment\noffline-group\nntop[0-9]\n* user key\n',encoding='utf-8')
    communities=receiver.Communities(community_list)
    batch=receiver.community_batch('offline-group')
    assert batch=='c-b897d2c8aa7662fd' and communities.lookup(batch)=='offline-group'
    assert communities.lookup(receiver.community_batch('ntop[0-9]')) is None
    community_storage=receiver.Storage(str(Path(temporary)/'community-logs'),None,communities)
    def community_row(batch_id,session):
        return (json.dumps(dict(schema=1,batch=batch_id,session=session))+'\n').encode()
    assert community_storage.store(batch,'a','00000000',community_row(batch,'a'))==201
    unknown=receiver.community_batch('someone-else')
    assert community_storage.store(unknown,'a','00000000',community_row(unknown,'a'))==403
    assert community_storage.store('offline','a','00000000',community_row('offline','a'))==403
    print('PASS: community mode accepts literal listed communities only; client proof vector matches')

    stun=load('stun','mikun2n-stun.py')
    import struct
    transaction=bytes(range(12))
    def request(software):
        attribute=struct.pack('!HH',0x8022,len(software))+software+b'\0'*((-len(software))%4)
        return struct.pack('!HHI',1,len(attribute),0x2112A442)+transaction+attribute
    tagged=request(b'MikuN2N/1 00ffaf5487d2')          # native tagged request, 48 bytes
    discover=request(b'MikuN2N/1 discover'.ljust(32))  # client discovery request, 56 bytes
    assert len(tagged)==48 and len(discover)==56
    assert stun.parse(tagged)==(transaction,'00ffaf5487d2') and stun.parse(discover)==(transaction,None)
    assert stun.parse(struct.pack('!HHI',1,0,0x2112A442)+transaction) is None      # anonymous request
    assert stun.parse(request(b'SomethingElse/1 00ffaf5487d2')) is None
    assert stun.parse(tagged[:-4]) is None and stun.parse(tagged+b'\0\0\0\0') is None
    v6_answer=stun.answer(transaction,[stun.address_attribute(stun.XOR_MAPPED,'2001:db8::1',50001,transaction,True)])
    v4_answer=stun.answer(transaction,[stun.address_attribute(stun.XOR_MAPPED,'203.0.113.9',50001,transaction,True),
        stun.address_attribute(stun.OTHER_ADDRESS,'2001:db8::2',3478,transaction,False)])
    assert len(v6_answer)<=len(tagged) and len(v4_answer)<=len(discover)
    limiter=stun.Limiter()
    assert all(limiter.allow(['p']) for _ in range(stun.BUCKET_SIZE)) and not limiter.allow(['p'])
    print('PASS: observer answers only tagged requests, never exceeds request size, and rate limits sources')
