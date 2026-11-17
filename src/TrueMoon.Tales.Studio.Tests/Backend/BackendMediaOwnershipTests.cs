using TrueMoon;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendMediaOwnershipTests
{
    [Fact]
    public void WireBuffer_CopiesManagedInputAndReturnsDeserializedRentalExactlyOnce()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var reply = new TrueMoon.Tales.Studio.Backend.BackendMediaReply { PixelBuffer = bytes };
        bytes[0] = 9;
        Assert.Equal(new byte[] { 1, 2, 3 }, reply.Pixels);
        var rented = MemoryPoolUtils.Create(3);
        new byte[] { 4, 5, 6 }.AsSpan().CopyTo(rented.Span);
        reply.PixelBuffer = rented;
        Assert.Equal(new byte[] { 4, 5, 6 }, reply.Pixels);
        Assert.False(MemoryPoolUtils.TryReturn(rented));
        Assert.Equal(new byte[] { 4, 5, 6 }, reply.PixelBuffer.ToArray());
    }
}
