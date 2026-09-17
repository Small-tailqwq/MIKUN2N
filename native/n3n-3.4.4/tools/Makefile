#
# This is not a standalone makefile, it must be called from the toplevel
# makefile to inherit the correct environment

LDLIBS+=-ln3n
LDLIBS+=$(LDLIBS_LOCAL)
LDLIBS+=$(LDLIBS_EXTRA)

TOOLS+=n3n-benchmark
TOOLS+=n3n-route
TOOLS+=n3n-portfwd
TOOLS+=n3n-decode
TOOLS+=crypto_helper

TESTS=tests-compress
TESTS+=tests-elliptic
TESTS+=tests-transform
TESTS+=tests-wire
TESTS+=tests-ipv6
TESTS+=tests-auth

.PHONY: all clean install
all: $(TOOLS) $(TESTS)

# This regression includes the transport implementation to intercept OS sends.
tests-ipv6: CPPFLAGS += -I../src
tests-ipv6: tests-ipv6.c ../src/mikun2n_ipv6.c ../src/mikun2n_ipv6_frag.h ../src/mikun2n_ipv6.h ../src/peer_info.h ../include/n2n_typedefs.h ../src/libn3n.a Makefile
	@echo "  CC      $@"
	@$(CC) $(CFLAGS) $(CPPFLAGS) $(LDFLAGS) $< $(LDLIBS) -o $@

# Remember to keep the two CC lines in sync
$(info CC is: $(CC) $(CFLAGS) $(CPPFLAGS) $(LDFLAGS) $$^ $(LDLIBS) -o $$@)
%: %.c
	@echo "  CC      $@"
	@$(CC) $(CFLAGS) $(CPPFLAGS) $(LDFLAGS) $^ $(LDLIBS) -o $@

# See comments in the topdir Makefile about how to generate coverage
# data.
gcov:
	gcov $(TOOLS) $(TESTS)

clean:
	rm -rf $(TOOLS) *.o *.dSYM *~
	rm -f $(TESTS) *.gcno *.gcda

install: $(TOOLS)
	$(INSTALL) -d $(CONFIG_SBINDIR)
	$(INSTALL_PROG) $(addsuffix $(EXE),$(TOOLS)) $(CONFIG_SBINDIR)/
