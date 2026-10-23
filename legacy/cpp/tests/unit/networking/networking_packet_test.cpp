// PacketWriter/PacketReader: 왕복 보존, 헤더 size 기록, 경계 검사(길이 초과·버퍼 부족).

#include <cstring>
#include <string>

#include "TestHarness.h"
#include "networking/Packet.h"

using namespace quiz;

namespace {

void test_writer_reader_roundtrip_preserves_fields() {
  // Arrange
  SendBuffer buffer(64);
  PacketWriter writer(buffer, 42);
  writer.Write(static_cast<uint64_t>(0x0102030405060708ULL));
  writer.WriteString("hello");
  writer.Write(static_cast<uint8_t>(7));
  CHECK(writer.Finish());

  // Act
  PacketReader reader(buffer.Data() + kPacketHeaderSize, buffer.Size() - kPacketHeaderSize);
  uint64_t number = 0;
  std::string text;
  uint8_t small = 0;
  const bool readNumber = reader.Read(number);
  const bool readText = reader.ReadString(text);
  const bool readSmall = reader.Read(small);

  // Assert
  CHECK(readNumber);
  CHECK(readText);
  CHECK(readSmall);
  CHECK_EQ(number, 0x0102030405060708ULL);
  CHECK(text == "hello");
  CHECK_EQ(small, 7);
  CHECK(reader.AtEnd());
}

void test_finish_writes_total_size_and_id_into_header() {
  // Arrange
  SendBuffer buffer(32);
  PacketWriter writer(buffer, 1234);
  writer.Write(static_cast<uint32_t>(1));
  writer.Write(static_cast<uint16_t>(2));

  // Act
  const bool finished = writer.Finish();
  PacketHeader header;
  std::memcpy(&header, buffer.Data(), sizeof(header));

  // Assert
  CHECK(finished);
  CHECK_EQ(header.size, kPacketHeaderSize + 4 + 2);
  CHECK_EQ(header.id, 1234);
  CHECK_EQ(buffer.Size(), static_cast<size_t>(header.size));
}

void test_reader_rejects_read_past_end() {
  // Arrange
  const uint8_t body[2] = {1, 2};
  PacketReader reader(body, sizeof(body));

  // Act
  uint32_t value = 0;
  const bool read = reader.Read(value);

  // Assert
  CHECK(!read);
  CHECK(!reader.Ok());
  CHECK(!reader.AtEnd());
}

void test_reader_rejects_string_longer_than_max() {
  // Arrange: 길이 접두 5, 본문 5바이트 — maxBytes 4로 읽으면 실패해야 한다.
  SendBuffer buffer(32);
  PacketWriter writer(buffer, 1);
  writer.WriteString("abcde");
  CHECK(writer.Finish());
  PacketReader reader(buffer.Data() + kPacketHeaderSize, buffer.Size() - kPacketHeaderSize);

  // Act
  std::string text;
  const bool read = reader.ReadString(text, 4);

  // Assert
  CHECK(!read);
  CHECK(!reader.Ok());
}

void test_reader_rejects_string_prefix_exceeding_body() {
  // Arrange: 길이 접두는 100이라고 하지만 본문은 2바이트뿐 (악의적/깨진 패킷).
  uint8_t body[4] = {100, 0, 'a', 'b'};
  PacketReader reader(body, sizeof(body));

  // Act
  std::string text;
  const bool read = reader.ReadString(text);

  // Assert
  CHECK(!read);
  CHECK(!reader.Ok());
}

void test_failed_read_poisons_subsequent_reads() {
  // Arrange
  const uint8_t body[1] = {9};
  PacketReader reader(body, sizeof(body));
  uint16_t tooBig = 0;
  CHECK(!reader.Read(tooBig));

  // Act: 원래는 읽을 수 있는 1바이트도 실패해야 한다.
  uint8_t fits = 0;
  const bool read = reader.Read(fits);

  // Assert
  CHECK(!read);
}

void test_writer_fails_when_buffer_too_small() {
  // Arrange: 헤더 4 + u32 4 = 8이 필요한데 용량은 6.
  SendBuffer buffer(6);
  PacketWriter writer(buffer, 5);

  // Act
  const bool wrote = writer.Write(static_cast<uint32_t>(1));
  const bool finished = writer.Finish();

  // Assert
  CHECK(!wrote);
  CHECK(!finished);
  CHECK(!writer.Ok());
}

void test_writer_rejects_string_over_limit() {
  // Arrange
  SendBuffer buffer(kMaxStringBytes + 64);
  PacketWriter writer(buffer, 5);
  const std::string tooLong(kMaxStringBytes + 1, 'x');

  // Act
  const bool wrote = writer.WriteString(tooLong);

  // Assert
  CHECK(!wrote);
  CHECK(!writer.Finish());
}

void test_two_packets_in_one_buffer_have_independent_headers() {
  // Arrange
  SendBuffer buffer(64);
  PacketWriter first(buffer, 1);
  first.Write(static_cast<uint8_t>(1));
  CHECK(first.Finish());
  PacketWriter second(buffer, 2);
  second.Write(static_cast<uint32_t>(2));
  CHECK(second.Finish());

  // Act
  PacketHeader h1, h2;
  std::memcpy(&h1, buffer.Data(), sizeof(h1));
  std::memcpy(&h2, buffer.Data() + h1.size, sizeof(h2));

  // Assert
  CHECK_EQ(h1.size, kPacketHeaderSize + 1);
  CHECK_EQ(h1.id, 1);
  CHECK_EQ(h2.size, kPacketHeaderSize + 4);
  CHECK_EQ(h2.id, 2);
  CHECK_EQ(buffer.Size(), static_cast<size_t>(h1.size + h2.size));
}

}  // namespace

int main() {
  RUN_TEST(test_writer_reader_roundtrip_preserves_fields);
  RUN_TEST(test_finish_writes_total_size_and_id_into_header);
  RUN_TEST(test_reader_rejects_read_past_end);
  RUN_TEST(test_reader_rejects_string_longer_than_max);
  RUN_TEST(test_reader_rejects_string_prefix_exceeding_body);
  RUN_TEST(test_failed_read_poisons_subsequent_reads);
  RUN_TEST(test_writer_fails_when_buffer_too_small);
  RUN_TEST(test_writer_rejects_string_over_limit);
  RUN_TEST(test_two_packets_in_one_buffer_have_independent_headers);
  return TestResult();
}
