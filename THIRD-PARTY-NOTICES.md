# Third-party notices

Ferry is under the MIT License (`LICENSE`). What it is built with or carries along has its own terms:

| Component | Where it is | License |
|---|---|---|
| .NET runtime and libraries | inside the app and the server's agent | MIT, © .NET Foundation and Contributors. https://github.com/dotnet/runtime/blob/main/LICENSE.TXT |
| Windows App SDK (WinUI 3) | inside the app | Microsoft's license for the Windows App SDK, `license.txt` in each of its NuGet packages. https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE |
| System.CommandLine | the helper and the agent | MIT, © .NET Foundation and Contributors. https://github.com/dotnet/command-line-api |
| MonoTorrent | the app and the agent, for the Torrents plugin | MIT, below |
| OpenSSH for Windows | `openssh\` beside the app, unchanged, from Microsoft's Win32-OpenSSH releases | OpenSSH's license (BSD-style, with parts under other permissive terms). https://github.com/PowerShell/openssh-portable/blob/latestw_all/LICENCE |

Ferry runs these when you have installed them yourself, and carries none of them: yt-dlp, ffmpeg, 7-Zip, aria2.

## MonoTorrent

MIT License

Copyright (C) 2006 Alan McGovern, Olivier Dufour

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
