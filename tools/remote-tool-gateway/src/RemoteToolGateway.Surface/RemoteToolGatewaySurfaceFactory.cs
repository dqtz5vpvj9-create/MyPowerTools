using Avalonia.Controls;
using MyPowerTools.AvaloniaSdk;

namespace RemoteToolGateway.Surface;

public sealed class RemoteToolGatewaySurfaceFactory : IMptAvaloniaSurfaceFactory
{
    public Control CreateSurface(MptAvaloniaSurfaceContext context) => new RemoteToolGatewayView(context);
}
