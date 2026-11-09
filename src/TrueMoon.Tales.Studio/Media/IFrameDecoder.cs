namespace TrueMoon.Tales.Studio.Media;

internal interface IFrameDecoder
{
    Task<byte[]> ReadAsync(Uri media, int frame, double fps, CancellationToken cancellationToken);
}
