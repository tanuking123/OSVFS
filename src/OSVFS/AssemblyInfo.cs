using System.Runtime.CompilerServices;

// Required by Native AOT to let the LibraryImport source generator emit struct
// marshalling for our P/Invokes (notably the WCHAR-buffer-bearing
// NOTIFYICONDATAW used by WindowsBalloonTipNotifier). Every other P/Invoke in
// this assembly already uses blittable parameters + StringMarshalling.Utf16,
// so disabling the legacy runtime marshalling layer is a no-op for them.
[assembly: DisableRuntimeMarshalling]
