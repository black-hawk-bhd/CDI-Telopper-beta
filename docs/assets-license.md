# Audio playback and license record

Date: 2026-08-01

CDI-Telopper does not bundle or redistribute alert sound files. Operators select
their own local WAV, MP3, or OGG files. The selected file paths
are stored in the local settings file; the audio files themselves are not
copied into diagnostics or release packages.

The application uses the following audio playback libraries:

- NAudio 2.3.0 — Mark Heath & Contributors, © Mark Heath 2026 — MIT License
- NAudio.Vorbis 1.5.0 — Andrew Ward, Copyright © Andrew Ward 2021 — MIT License
- NVorbis 0.10.4 — Andrew Ward, Copyright © 2020 Andrew Ward — MIT License

The MIT License applies to the libraries listed above:

Copyright notices are listed above.

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
