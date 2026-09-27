using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MyPowerTools.Tests")]
// The mobile layout suite drives the touch shell and the workspace controller directly so page
// navigation and external activations can be verified without starting a Runner.
[assembly: InternalsVisibleTo("MobileLayout.Tests")]
