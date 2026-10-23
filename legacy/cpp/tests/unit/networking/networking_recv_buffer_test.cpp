// RecvBuffer: 수신 포지션 추적, 압축(Clean), 분할 수신된 패킷 헤더의 복원.

#include <cstring>
#include <vector>

#include "TestHarness.h"
#include "networking/Packet.h"
#include "networking/RecvBuffer.h"

using namespace quiz;

namespace {

void test_write_then_read_tracks_positions() {
  // Arrange
  RecvBuffer buffer(64);

  // Act
  const bool wrote = buffer.OnWrite(10);
  const bool read = buffer.OnRead(4);

  // Assert
  CHECK(wrote);
  CHECK(read);
  CHECK_EQ(buffer.DataSize(), 6u);
  CHECK_EQ(buffer.FreeSize(), 54u);
}

void test_write_beyond_free_space_fails() {
  // Arrange
  RecvBuffer buffer(16);
  buffer.OnWrite(10);

  // Act
  const bool wrote = buffer.OnWrite(7);

  // Assert
  CHECK(!wrote);
  CHECK_EQ(buffer.DataSize(), 10u);
}

void test_read_beyond_data_fails() {
  // Arrange
  RecvBuffer buffer(16);
  buffer.OnWrite(5);

  // Act
  const bool read = buffer.OnRead(6);

  // Assert
  CHECK(!read);
  CHECK_EQ(buffer.DataSize(), 5u);
}

void test_clean_resets_when_empty() {
  // Arrange
  RecvBuffer buffer(16);
  buffer.OnWrite(12);
  buffer.OnRead(12);

  // Act
  buffer.Clean();

  // Assert
  CHECK_EQ(buffer.DataSize(), 0u);
  CHECK_EQ(buffer.FreeSize(), 16u);
}

void test_clean_compacts_partial_data_to_front() {
  // Arrange
  RecvBuffer buffer(16);
  const uint8_t payload[8] = {1, 2, 3, 4, 5, 6, 7, 8};
  std::memcpy(buffer.WritePos(), payload, sizeof(payload));
  buffer.OnWrite(sizeof(payload));
  buffer.OnRead(5);  // 6,7,8 남음

  // Act
  buffer.Clean();

  // Assert
  CHECK_EQ(buffer.DataSize(), 3u);
  CHECK_EQ(buffer.FreeSize(), 13u);
  CHECK_EQ(buffer.ReadPos()[0], 6);
  CHECK_EQ(buffer.ReadPos()[1], 7);
  CHECK_EQ(buffer.ReadPos()[2], 8);
}

/// Session::ProcessPackets와 같은 규칙으로 완전한 패킷만 꺼낸다. 꺼낸 패킷 id 목록을 돌려준다.
std::vector<uint16_t> ExtractPackets(RecvBuffer& buffer) {
  std::vector<uint16_t> ids;
  while (buffer.DataSize() >= kPacketHeaderSize) {
    PacketHeader header;
    std::memcpy(&header, buffer.ReadPos(), sizeof(header));
    if (buffer.DataSize() < header.size) break;
    ids.push_back(header.id);
    buffer.OnRead(header.size);
  }
  buffer.Clean();
  return ids;
}

void test_header_split_across_two_writes_reassembles() {
  // Arrange: 패킷 A(8바이트, id 7)와 B(6바이트, id 9)를 이어붙인 스트림을 3바이트 / 나머지로 쪼개 넣는다.
  RecvBuffer buffer(64);
  SendBuffer wire(32);
  {
    PacketWriter a(wire, 7);
    a.Write(static_cast<uint32_t>(0xAABBCCDD));
    CHECK(a.Finish());
    PacketWriter b(wire, 9);
    b.Write(static_cast<uint16_t>(0x1234));
    CHECK(b.Finish());
  }
  CHECK_EQ(wire.Size(), 14u);

  // Act 1: 헤더가 잘린 3바이트만 도착
  std::memcpy(buffer.WritePos(), wire.Data(), 3);
  buffer.OnWrite(3);
  const auto first = ExtractPackets(buffer);

  // Act 2: 나머지 11바이트 도착
  std::memcpy(buffer.WritePos(), wire.Data() + 3, wire.Size() - 3);
  buffer.OnWrite(wire.Size() - 3);
  const auto second = ExtractPackets(buffer);

  // Assert
  CHECK_EQ(first.size(), 0u);
  CHECK_EQ(second.size(), 2u);
  if (second.size() == 2) {
    CHECK_EQ(second[0], 7u);
    CHECK_EQ(second[1], 9u);
  }
  CHECK_EQ(buffer.DataSize(), 0u);
}

void test_two_packets_in_one_write_both_extracted() {
  // Arrange
  RecvBuffer buffer(64);
  SendBuffer wire(32);
  PacketWriter a(wire, 1);
  CHECK(a.Finish());
  PacketWriter b(wire, 2);
  b.Write(static_cast<uint8_t>(0xFF));
  CHECK(b.Finish());

  // Act
  std::memcpy(buffer.WritePos(), wire.Data(), wire.Size());
  buffer.OnWrite(wire.Size());
  const auto ids = ExtractPackets(buffer);

  // Assert
  CHECK_EQ(ids.size(), 2u);
  CHECK_EQ(buffer.DataSize(), 0u);
}

}  // namespace

int main() {
  RUN_TEST(test_write_then_read_tracks_positions);
  RUN_TEST(test_write_beyond_free_space_fails);
  RUN_TEST(test_read_beyond_data_fails);
  RUN_TEST(test_clean_resets_when_empty);
  RUN_TEST(test_clean_compacts_partial_data_to_front);
  RUN_TEST(test_header_split_across_two_writes_reassembles);
  RUN_TEST(test_two_packets_in_one_write_both_extracted);
  return TestResult();
}
