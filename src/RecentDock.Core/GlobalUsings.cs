// Explicit rather than relying on ImplicitUsings alone.
//
// Enabling <UseWPF> changes which implicit usings the SDK injects, and System.IO
// stopped being available, which broke File/Directory/Path/IOException across the
// library. Declaring it here keeps the library building regardless of how the
// SDK's implicit-using set evolves.
global using System.IO;
