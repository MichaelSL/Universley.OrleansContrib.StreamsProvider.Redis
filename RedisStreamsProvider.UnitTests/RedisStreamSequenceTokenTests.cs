using Moq;
using Orleans.Streams;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;

namespace RedisStreamsProvider.UnitTests
{
    public class RedisStreamSequenceTokenTests
    {
        [Fact]
        public void Constructor_ShouldInitializeProperties_FromRedisValue()
        {
            // Arrange
            var redisValue = new RedisValue("123-456");

            // Act
            var token = new RedisStreamSequenceToken(redisValue);

            // Assert
            Assert.Equal(123, token.SequenceNumber);
            Assert.Equal(456, token.EventIndex);
        }

        [Fact]
        public void Constructor_ShouldInitializeProperties_FromParameters()
        {
            // Arrange
            long sequenceNumber = 123;
            int eventIndex = 456;

            // Act
            var token = new RedisStreamSequenceToken(sequenceNumber, eventIndex);

            // Assert
            Assert.Equal(sequenceNumber, token.SequenceNumber);
            Assert.Equal(eventIndex, token.EventIndex);
        }

        [Fact]
        public void CompareTo_ShouldReturnZero_ForEqualTokens()
        {
            // Arrange
            var token1 = new RedisStreamSequenceToken(123, 456);
            var token2 = new RedisStreamSequenceToken(123, 456);

            // Act
            var result = token1.CompareTo(token2);

            // Assert
            Assert.Equal(0, result);
        }

        [Fact]
        public void CompareTo_ShouldReturnPositive_ForGreaterToken()
        {
            // Arrange
            var token1 = new RedisStreamSequenceToken(123, 456);
            var token2 = new RedisStreamSequenceToken(123, 455);

            // Act
            var result = token1.CompareTo(token2);

            // Assert
            Assert.True(result > 0);
        }

        [Fact]
        public void CompareTo_ShouldReturnNegative_ForLesserToken()
        {
            // Arrange
            var token1 = new RedisStreamSequenceToken(123, 455);
            var token2 = new RedisStreamSequenceToken(123, 456);

            // Act
            var result = token1.CompareTo(token2);

            // Assert
            Assert.True(result < 0);
        }

        [Fact]
        public void Equals_ShouldReturnTrue_ForEqualTokens()
        {
            // Arrange
            var token1 = new RedisStreamSequenceToken(123, 456);
            var token2 = new RedisStreamSequenceToken(123, 456);

            // Act
            var result = token1.Equals(token2);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void Equals_ShouldReturnFalse_ForDifferentTokens()
        {
            // Arrange
            var token1 = new RedisStreamSequenceToken(123, 456);
            var token2 = new RedisStreamSequenceToken(123, 457);

            // Act
            var result = token1.Equals(token2);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void CompareTo_ShouldThrowArgumentNullException_ForNullToken()
        {
            // Arrange
            var token = new RedisStreamSequenceToken(123, 456);

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => token.CompareTo(null));
        }

        [Fact]
        public void CompareTo_ShouldThrowArgumentException_ForInvalidTokenType()
        {
            // Arrange
            var token = new RedisStreamSequenceToken(123, 456);
            var invalidToken = new Mock<StreamSequenceToken>().Object;

            // Act & Assert
            Assert.Throws<ArgumentException>(() => token.CompareTo(invalidToken));
        }
    }
}
