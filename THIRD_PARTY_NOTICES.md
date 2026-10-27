# Third-party notices

The repository's MIT license covers its original application code, scripts and documentation. It does not grant rights to third-party game content, trademarks or dependencies.

## Game reference material

`tests/fixtures/*.png` contains screenshots of Neverness to Everness / 异环, and `Assets/arrow-template.png` is an extracted player-marker reference. Game artwork, interfaces, characters and trademarks remain the property of their respective rights holders. These files are regression references and are excluded from this project's MIT grant.

The application icon was generated with ImageGen using the game's minimap as a visual reference. Any rights in underlying game material remain with its owners; this repository makes no claim to ownership of those elements. The project is an independent utility and is not an official game product.

## Windows SDK .NET projections

The application uses Microsoft.Windows.SDK.NET.Ref 10.0.26100.57, including Microsoft.Windows.SDK.NET.dll and WinRT.Runtime.dll. Copyright Microsoft Corporation. The package identifies the [Windows SDK license](https://aka.ms/WinSDKLicenseURL). C#/WinRT is distributed under the MIT license; its notice is reproduced in `LICENSES/CsWinRT-LICENSE.txt`.

## Bundled .NET runtime

The self-contained release includes Microsoft's .NET and Windows Desktop runtime. Their upstream license and third-party notice files are copied from the resolved runtime packages into the release's `LICENSES/` directory. Those files retain their original terms and copyright notices. The framework-dependent release does not distribute the .NET runtime.
