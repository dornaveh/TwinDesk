# Third-party components

Bundled components retain their own copyright notices and licenses.
No project-wide license has been selected for TwinDesk's own code.

- **NAudio.Wasapi and NAudio.Core 3.1.0**, Mark Heath, MIT. Used internally for
  Windows WASAPI audio interoperability. Restored by NuGet during the Windows
  build. [License](windows/third_party/naudio/LICENSE.txt),
  [upstream](https://github.com/naudio/NAudio).
- **System.Numerics.Tensors 9.0.0**, .NET Foundation and contributors, MIT.
  Transitive NuGet dependency. [License](windows/third_party/dotnet-tensors/LICENSE.TXT)
  and [notices](windows/third_party/dotnet-tensors/THIRD-PARTY-NOTICES.TXT).
- **m1ddc**, MIT, bundled source for the optional Apple Silicon display-control
  helper. [License](mac/third_party/m1ddc/LICENSE),
  [upstream](https://github.com/waydabber/m1ddc).

Windows publish output contains its dependency license files. The Mac build
includes the m1ddc license in the app bundle. No third-party executable needs
to be installed or configured separately by the user.
