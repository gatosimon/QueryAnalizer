QueryAnalyzer - WPF (.NET 4.5) ODBC Query Analyzer (fixed)
-----------------------------------------------

Fix applied:
- App.xaml is now marked as <ApplicationDefinition> in the .csproj so the WPF build generates the required 'Main' entry point.
- This eliminates CS5001: 'no static Main method found' when building.

What to do:
1. Unzip and open QueryAnalyzer_fixed.sln in Visual Studio.
2. Build and Run. Ensure you have the proper ODBC drivers installed.

If you still see CS5001 after this:
- Make sure the project type is WPF (OutputType WinExe and UseWPF true already set).
- Ensure Visual Studio has Windows Desktop components installed (WPF tools).
- Let me know the exact Visual Studio version and the full error list.

Enjoy.
