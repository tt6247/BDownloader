# Third-party components

This project is licensed under GPL-3.0. Third-party components retain their own licenses.

| Component | Version | License and source |
|---|---|---|
| Newtonsoft.Json | Assembly 13.0.0.0 | [MIT](source/BDownloader/lib/Newtonsoft.Json-LICENSE.txt), [source](https://github.com/JamesNK/Newtonsoft.Json) |
| QRCoder | 1.4.3 | [MIT](source/BDownloader/lib/QRCoder-LICENSE.txt), [source](https://github.com/codebude/QRCoder/tree/v1.4.3) |
| FFmpeg | n5.0.1-4-ga5ebb3d25e-20220508, supplied static GPL build | [License](source/BDownloader/lib/FFmpeg_LICENSE.txt), [source](https://github.com/FFmpeg/FFmpeg/tree/a5ebb3d25e) |
| yt-dlp | 2026.08.19 | [Release and bundled license notices](https://github.com/yt-dlp/yt-dlp/releases/tag/2026.08.19) |
| QuickJS-NG | 0.17.0 | [MIT and source](https://github.com/quickjs-ng/quickjs/tree/v0.17.0) |

The prepared FFmpeg, Newtonsoft.Json and QRCoder binaries are provided as the separately checksummed `BDownloader-build-dependencies.zip` release asset. They are excluded from Git history. Their original license texts are committed under `source/BDownloader/lib/`.

The media preparation scripts pin upstream versions and SHA-256 digests. The single-file application includes the applicable license notices inside its verified runtime payload.
