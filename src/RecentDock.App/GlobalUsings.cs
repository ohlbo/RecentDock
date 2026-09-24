// Alias required because this project references BOTH WPF and WinForms:
//   WPF      : System.Windows.Application, System.Windows.MessageBox
//   WinForms : System.Windows.Forms.Application, System.Windows.Forms.MessageBox
//
// WinForms is pulled in only for NotifyIcon. Every other use of these two type
// names in this project means the WPF one, so the alias resolves the ambiguity
// once here instead of qualifying at every call site.
//
// If a WinForms MessageBox is ever needed, qualify it as
// System.Windows.Forms.MessageBox.
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;

// Same reason: ListViewItem exists in both WPF and WinForms namespaces.
global using ListViewItem = System.Windows.Controls.ListViewItem;

// And again for Color, which System.Drawing also defines. Every use in this project
// means the WPF one.
global using Color = System.Windows.Media.Color;

// System.Drawing also defines Size and Point. WPF's are meant throughout.
global using Size = System.Windows.Size;
global using Point = System.Windows.Point;

// Explicit because UseWindowsForms changes which implicit usings the SDK injects,
// and System.IO stopped being available (same effect seen in RecentDock.Core).
global using System.IO;
