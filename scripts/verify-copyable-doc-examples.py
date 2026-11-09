#!/usr/bin/env python3
"""Compile complete quickstart and test-double examples directly from their Markdown fences."""
from pathlib import Path
import re
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
VERSIONS = {
    item.attrib["Include"]: item.attrib["Version"]
    for item in ET.parse(ROOT / "Directory.Packages.props").iter("PackageVersion")
}


def blocks(path, heading):
    text = (ROOT / path).read_text()
    section = text.split(heading + "\n", 1)[1]
    section = re.split(r"^#{1,3} ", section, maxsplit=1, flags=re.MULTILINE)[0]
    return re.findall(r"```csharp\n(.*?)```", section, flags=re.DOTALL)


def compile_example(directory, name, source, packages=(), executable=False):
    project = directory / name
    project.mkdir()
    references = (
        f'<ProjectReference Include="{ROOT}/src/TemporalCommunity.Extensions.Agents/'
        'TemporalCommunity.Extensions.Agents.csproj" />'
    )
    references += "".join(
        f'<PackageReference Include="{package}" Version="{VERSIONS[package]}" />'
        for package in packages
    )
    (project / "Example.csproj").write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
        '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
        '<Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors>'
        '<IsPackable>false</IsPackable><GenerateDocumentationFile>false</GenerateDocumentationFile>'
        + ('<OutputType>Exe</OutputType>' if executable else '')
        + '</PropertyGroup><ItemGroup>' + references + '</ItemGroup></Project>'
    )
    for filename, code in source.items():
        (project / filename).write_text(code)
    subprocess.run(
        ["dotnet", "build", str(project / "Example.csproj"), "--nologo", "--verbosity", "quiet"],
        check=True,
    )
    print(f"PASS: copied {name} example", flush=True)


def main():
    with tempfile.TemporaryDirectory(prefix="temporal-doc-examples-") as temporary:
        directory = Path(temporary).resolve()
        quickstart = "docs/how-to/MAF/quickstart.md"
        registration = blocks(quickstart, "## 1. Register the agent on a worker")[0]
        call, weather = blocks(quickstart, "## 2. Call it through the keyed MAF proxy")
        packages = tuple(
            package for package in re.findall(
                r"^dotnet add package ([\w.]+)$", (ROOT / quickstart).read_text(), re.MULTILINE
            ) if package != "TemporalCommunity.Extensions.Agents"
        )
        compile_example(directory, "quickstart", {"Program.cs": registration + call + weather},
                        packages, executable=True)
        usings = "\n".join(re.findall(r"^using .*?;", registration, re.MULTILINE)) + "\n"
        split = blocks(quickstart, "## 3. Split the client from the worker")[0]
        compile_example(directory, "split-client", {"Program.cs": usings + split},
                        packages, executable=True)

        maf = "docs/how-to/MAF/testing-agents.md"
        agent, test = blocks(maf, "### StubAIAgent — Minimal Test Double")
        spy = blocks(maf, "### CapturingChatClient — Spy for ChatOptions")[0]
        compile_example(directory, "maf-test-doubles", {
            "Agent.cs": agent,
            "Tests.cs": "using Microsoft.Agents.AI;\nusing Microsoft.Extensions.AI;\n"
            "using Xunit;\npublic class AgentTests {\n" + test + "\n}",
            "Spy.cs": "using Microsoft.Extensions.AI;\n" + spy,
        }, ("xunit",))

        meai = "docs/how-to/MEAI/testing.md"
        service, stub, test, registration = blocks(meai, "### Stub `IDurableChatSessionClient`")
        usings = (
            "using Microsoft.Extensions.AI;\nusing Microsoft.Extensions.DependencyInjection;\n"
            "using TemporalCommunity.Extensions.AI;\nusing TemporalCommunity.Extensions.AI.Session;\n"
            "using Xunit;\n"
        )
        compile_example(directory, "meai-test-double", {
            "Stub.cs": stub,
            "Service.cs": usings + service,
            "Tests.cs": usings + "public class ChatTests {\n" + test
            + "\nprivate void Register() { var services = new ServiceCollection();\n"
            + registration + "\n}\n}",
        }, ("xunit",))


if __name__ == "__main__":
    main()
