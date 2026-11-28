using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MyPowerTools.Tests")]
// The mobile layout suite drives the touch shell and the workspace controller directly so page
// navigation and external activations can be verified without starting a Runner.
[assembly: InternalsVisibleTo("MobileLayout.Tests")]
// Headless screenshots render the recovery page in a mid-ladder state.
[assembly: InternalsVisibleTo("mpt-visual-test")]
