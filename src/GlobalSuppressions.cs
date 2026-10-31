using System.Diagnostics.CodeAnalysis;

// 本程序只支持 Windows：WinForms、NTFS 目录联接、注册表、WScript.Shell。
// 因此 CA1416（跨平台 API 可用性）在本程序集内不适用。
[assembly: SuppressMessage(
    "Interoperability",
    "CA1416:Validate platform compatibility",
    Justification = "Windows-only application (WinForms, NTFS junctions, registry, WScript.Shell).",
    Scope = "namespaceanddescendants",
    Target = "~N:VSCodeDataMover")]
