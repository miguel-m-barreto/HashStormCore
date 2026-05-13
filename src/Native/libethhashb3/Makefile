CFLAGS += -g -Wall -c -fPIC -O2 -D_FILE_OFFSET_BITS=64 -D_LARGEFILE_SOURCE -Wno-pointer-sign -Wno-char-subscripts -Wno-unused-variable -Wno-unused-function -Wno-strict-aliasing -Wno-discarded-qualifiers -Wno-unused-const-variable $(CPU_FLAGS) $(HAVE_FEATURE)
CXXFLAGS += -g -Wall -fPIC -fpermissive -O2 -Wno-char-subscripts -Wno-unused-variable -Wno-unused-function -Wno-strict-aliasing -Wno-sign-compare -std=c++17 $(CPU_FLAGS) $(HAVE_FEATURE)
LDFLAGS += -shared
TARGET = libethhashb3.so

OBJECTS = internal.o io.o io_posix.o sha3.o exports.o

all: $(TARGET)

$(TARGET): $(OBJECTS)
	$(CXX) $(LDFLAGS) -o $@ $^ $(LDLIBS)

.PHONY: clean

clean:
	$(RM) $(TARGET) $(OBJECTS)
