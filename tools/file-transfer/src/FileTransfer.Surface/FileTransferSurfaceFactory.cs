using Avalonia.Controls;
using MyPowerTools.AvaloniaSdk;

namespace FileTransfer.Surface;

public sealed class FileTransferSurfaceFactory : IMptAvaloniaSurfaceFactory
{
    public Control CreateSurface(MptAvaloniaSurfaceContext context) => new TransferView(context);
}
