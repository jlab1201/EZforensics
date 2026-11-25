# Third-party notices

EZ Forensics is MIT licensed (see [LICENSE](LICENSE)). It uses the software below.

## Tools downloaded at runtime

These are **not bundled**. The setup wizard downloads them to your machine, from their official sources, only after you accept their licences. Each stays under its own licence.

| Tool | License | Link |
|---|---|---|
| Eric Zimmerman's tools | MIT | https://github.com/EricZimmerman |
| log2timeline / plaso | Apache 2.0 | https://github.com/log2timeline/plaso/blob/main/LICENSE |
| Volatility 3 | Volatility Software License 1.0 | https://github.com/volatilityfoundation/volatility3/blob/develop/LICENSE.txt |
| Zeek | BSD 3-Clause | https://github.com/zeek/zeek/blob/master/COPYING |
| RITA | GPL-3.0 | https://github.com/activecm/rita/blob/main/LICENSE |
| ClickHouse (used by RITA) | Apache 2.0 | https://github.com/ClickHouse/ClickHouse/blob/master/LICENSE |
| Ubuntu Base (private WSL environment) | Ubuntu free software licences | https://ubuntu.com/legal/intellectual-property-policy |
| Microsoft .NET 9 runtime | MIT | https://github.com/dotnet/runtime/blob/main/LICENSE.TXT |
| Python (private copy) | Python Software Foundation License | https://docs.python.org/3/license.html |
| Ollama (optional local AI, installed separately by the user) | MIT | https://github.com/ollama/ollama/blob/main/LICENSE |
| Qwen3 models (optional local AI, pulled by Ollama) | Apache 2.0 | https://huggingface.co/Qwen |
| Detect It Easy (DIE), for Explore | MIT | https://github.com/horsicq/Detect-It-Easy/blob/master/LICENSE |
| ExifTool, for Explore | Perl Artistic License / GPL | https://exiftool.org/#license |
| CyberChef, for Explore | Apache 2.0 | https://github.com/gchq/CyberChef/blob/master/LICENSE |

## NuGet packages (compiled into the app)

| Package | Version | License | Link |
|---|---|---|---|
| Anthropic (SDK) | 13.3.0 | Apache-2.0 | https://docs.anthropic.com/claude/reference/ |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet |
| Markdig | 0.38.0 | BSD-2-Clause | https://github.com/lunet-io/markdig |
| Microsoft.Data.Sqlite | 8.0.11 | MIT | https://learn.microsoft.com/dotnet/standard/data/sqlite/ |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.14 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| System.Security.Cryptography.Pkcs | 8.0.2 | MIT | https://github.com/dotnet/runtime |
| Microsoft.ML.OnnxRuntime | 1.20.1 | MIT | https://github.com/Microsoft/onnxruntime |
| Microsoft.ML.Tokenizers | 1.0.2 | MIT | https://dot.net/ml |
| Microsoft.Web.WebView2 | 1.0.2903.40 | Microsoft Software License Terms (BSD-style) | https://www.nuget.org/packages/Microsoft.Web.WebView2 |
| PdfPig (UglyToad.PdfPig) | 0.1.16 | Apache-2.0 | https://github.com/UglyToad/PdfPig |
| PeNet | 5.1.0 | Apache-2.0 | https://github.com/secana/PeNet |
| Registry (Eric Zimmerman) | 2026.5.0 | MIT | https://github.com/EricZimmerman/Registry |
| SharpCompress | 0.50.5 | MIT | https://github.com/adamhathcock/sharpcompress |
| Sylvan.Data.Csv | 1.4.4 | MIT | https://github.com/MarkPflug/Sylvan |
| System.Numerics.Tensors | 8.0.0 | MIT | https://github.com/dotnet/runtime |
| System.Security.Cryptography.ProtectedData | 8.0.0 | MIT | https://github.com/dotnet/runtime |
| Velopack | 1.2.161 | MIT | https://github.com/velopack/velopack |
| YamlDotNet | 18.1.0 | MIT | https://github.com/aaubry/YamlDotNet |

The Explore hex viewer is written in-house; no third-party hex control is used.

The full licence text of each package ships with the package on nuget.org.

## JavaScript libraries (bundled in the 3D Graph page)

Vendored in `src/EzForensics.App/Assets/brain/vendor/`, each with its own LICENSE file. Only import paths were changed.

| Library | Version | License |
|---|---|---|
| 3d-force-graph | 1.80.1 | MIT |
| three | 0.186.1 | MIT |
| three-forcegraph | 1.43.6 | MIT |
| three-render-objects | 1.44.1 | MIT |
| three-spritetext | 1.10.0 | MIT |
| @tweenjs/tween.js | 25.0.0 | MIT |
| @babel/runtime | 7.29.10 | MIT |
| accessor-fn, d3-binarytree, d3-force-3d, d3-octree, data-bind-mapper, float-tooltip, kapsule | 1.5.3, 1.0.2, 3.0.6, 1.1.0, 1.0.3, 1.8.0, 1.16.3 | MIT |
| d3-array, d3-color, d3-dispatch, d3-format, d3-interpolate, d3-quadtree, d3-scale, d3-selection, d3-time, d3-time-format, d3-timer | 3.2.5, 3.1.1, 3.0.2, 3.1.3, 3.0.2, 3.0.2, 4.0.3, 3.0.1, 3.1.1, 4.1.1, 3.0.2 | ISC |
| d3-scale-chromatic | 3.1.1 | ISC (colour schemes: Apache-2.0, ColorBrewer) |
| lodash-es | 4.18.1 | MIT |
| ngraph.events, ngraph.graph | 1.4.0, 20.1.2 | BSD-3-Clause |
| polished | 4.3.1 | MIT |
| preact | 11.2.0 | MIT |
| tinycolor2 | 1.6.0 | MIT |

## Embedding model (downloaded at build time)

bge-small-en-v1.5 by BAAI (MIT), quantized ONNX build from Xenova/bge-small-en-v1.5 on Hugging Face. `build/get-embedding-model.ps1` downloads it from a pinned commit and checks the SHA-256. It is not stored in this repository. https://huggingface.co/BAAI/bge-small-en-v1.5

## Bundled data

MITRE ATT&CK Enterprise 20.1 technique list (`src/EzForensics.Core/Resources/mitre/`), (c) The MITRE Corporation, used under the ATT&CK Terms of Use. https://attack.mitre.org/resources/terms-of-use/

## Optional external services

Anthropic, OpenAI and OpenRouter are optional services you connect to yourself, with your own key. Ollama is a separate app you install yourself and connect to; EZ Forensics only downloads models through it. Nothing from them is bundled with EZ Forensics. Their terms apply to your use.
