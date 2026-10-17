"""Compile optional scheduler C# against real platform references; does not build an APK/app or qualify OS delivery."""
import argparse
import json
from pathlib import Path
import subprocess
from xml.sax.saxutils import escape
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "src/Samples/Bsync.Samples.NativeBackground"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("platform", choices=["android", "ios"])
    parser.add_argument("--android-ref", type=Path, help="Installed Android framework ref/net10.0 directory")
    parser.add_argument("--ios-package", type=Path, help="Official Microsoft.iOS.Ref.net10.0 package archive")
    args = parser.parse_args()
    output = ROOT / "artifacts" / f"{args.platform}-binding-check"
    output.mkdir(parents=True, exist_ok=True)
    references = [ROOT / "src/Bsync/Bsync/bin/Release/net10.0/Bsync.dll"]
    if args.platform == "android":
        if args.android_ref is None:
            parser.error("--android-ref is required; first restore the sample with NativeBackgroundTarget=net10.0-android")
        assets = json.loads((PROJECT / "obj/project.assets.json").read_text(encoding="utf-8"))
        target = next((value for key, value in assets["targets"].items() if "android" in key), None)
        if target is None:
            parser.error("Restore the Android target first; the portable target's assets do not contain WorkManager bindings")
        roots = [Path(path) for path in assets["packageFolders"]]
        for name, library in target.items():
            if library.get("type") != "package":
                continue
            identity = assets["libraries"][name]["path"]
            for relative in library.get("compile", {}):
                if relative.endswith(".dll"):
                    references.append(next(path / identity / relative for path in roots if (path / identity / relative).is_file()))
        references.extend(args.android_ref.glob("*.dll"))
        platform = "android23.0"
    else:
        if args.ios_package is None:
            parser.error("--ios-package is required")
        with zipfile.ZipFile(args.ios_package) as archive:
            # Extract exactly one known reference, never arbitrary archive paths.
            reference = output / "Microsoft.iOS.dll"
            reference.write_bytes(archive.read("ref/net10.0/Microsoft.iOS.dll"))
        references.append(reference)
        platform = "ios13.0"
    if len(references) < 2 or not all(path.is_file() for path in references):
        raise ValueError("Build Bsync Release and provide complete platform reference paths first")
    xml = ('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
           '<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>'
           f'<DefineConstants>{args.platform.upper()}</DefineConstants><TreatWarningsAsErrors>true</TreatWarningsAsErrors>'
           '<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup>')
    for reference in references:
        xml += f'<Reference Include="{escape(reference.stem)}"><HintPath>{escape(str(reference.resolve()))}</HintPath></Reference>'
    for source in PROJECT.glob("*.cs"):
        xml += f'<Compile Include="{escape(str(source))}" />'
    xml += '<Compile Include="Platform.cs" /></ItemGroup></Project>'
    (output / "Check.csproj").write_text(xml, encoding="utf-8")
    (output / "Platform.cs").write_text(f'[assembly: System.Runtime.Versioning.SupportedOSPlatform("{platform}")]', encoding="utf-8")
    return subprocess.call(["dotnet", "build", str(output / "Check.csproj"), "-c", "Release"], cwd=ROOT)


if __name__ == "__main__":
    raise SystemExit(main())
