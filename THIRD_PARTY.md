# Third-party components

WMAI itself is licensed under the GNU GPL v3.0 (see [LICENSE](LICENSE)).
It uses or ships the following third-party components.

## Bouncy Castle C# 1.8.10

TLS 1.2, X.509 certificate validation and zlib. Not stored in this repository:
`vendor/fetch_bc.py` downloads the official release tag from
<https://github.com/bcgit/bc-csharp> and applies three small patches for the
.NET Compact Framework (described in that script). The release kit ships the
compiled `BouncyCastle.Crypto.dll`.

> The Bouncy Castle License
> Copyright (c) 2000-2021 The Legion of the Bouncy Castle Inc. (https://www.bouncycastle.org)
>
> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sub license, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in
> all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

## PocketGCC 1.50 (GCC 3.2.2, binutils 2.13.2.1)

The on-device compiler, by Vitaliy Pronkin and contributors,
<https://sourceforge.net/projects/pocketgcc/>. GCC and binutils are licensed
under the GNU GPL (version 2 or later).

The release kit ships `cc1plus`, `cpp0`, `as`, `ld`, `windres` and `ar`
**rebuilt by WMAI** from PocketGCC's source package with the change in
[compiler/](compiler/) (`--stdout=FILE` / `--stderr=FILE`). Their complete
corresponding source is published with every release as
`pocketgcc-wmai-src-<version>.zip` (PocketGCC's source tree with the WMAI
patch applied) and can be reproduced from the upstream package with
`compiler/apply_wce_stdio.py`.

The kit's `pgcc\include` and `pgcc\lib` folders and the `pgcc\samp` sample
are copied unchanged from the PocketGCC 1.50 package. As its author notes,
the headers and import libraries were taken from Microsoft's Pocket PC 2002
SDK, and the sample is Microsoft sample code; they are distributed as part of
PocketGCC under the terms that apply to them.

## Mozilla CA certificate list

`WMAI.roots.idx` is generated from the Mozilla CA certificate bundle as
published by the curl project (<https://curl.se/docs/caextract.html>), licensed
under the Mozilla Public License 2.0. `WMAI.roots` holds root certificates
(Amazon, Google Trust Services, ISRG) obtained from their publishers.

## Microsoft components (not included)

WMAI runs on Microsoft's .NET Compact Framework 3.5 and Windows Mobile system
libraries, which are part of the device, not of this project. The NETCF
reference assemblies used by `tools/cfcheck.ps1` are copied from your own
device by `tools/pull_cfref.py` and are not redistributed.
