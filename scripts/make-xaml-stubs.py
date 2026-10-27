"""Platzhalter für den Code, den der XAML-Compiler unter Windows erzeugt (Felder für x:Name, InitializeComponent),
damit sich der C#-Teil der Windows-App auf dem Mac prüfen lässt. Aufruf über scripts/check-app.sh."""
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

app = Path(sys.argv[1])
out = Path(sys.argv[2])
out.mkdir(parents=True, exist_ok=True)

X = "http://schemas.microsoft.com/winfx/2006/xaml"
DEFAULT = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"
# Inhalte dieser Elemente bekommen keine Felder in der Klasse.
TEMPLATES = {"DataTemplate", "ControlTemplate", "ItemsPanelTemplate", "Style", "VisualStateGroup", "VisualState",
             "VisualStateManager.VisualStateGroups"}


def namespaces(path):
    result = {}
    for event, (prefix, uri) in ET.iterparse(path, events=["start-ns"]):
        result[uri] = prefix
    return result


for xaml in sorted(app.rglob("*.xaml")):
    if "obj" in xaml.parts or "bin" in xaml.parts:
        continue
    tree = ET.parse(xaml)
    root = tree.getroot()
    cls = root.get(f"{{{X}}}Class")
    if not cls:
        continue
    fields = []

    def visit(element, inside_template):
        tag = element.tag
        uri, local = tag[1:].split("}") if tag.startswith("{") else ("", tag)
        name = element.get(f"{{{X}}}Name")
        if name and not inside_template and element is not root:
            if uri == DEFAULT:
                type_name = local  # über die using-Zeilen aufgelöst
            elif uri.startswith("using:"):
                type_name = f"global::{uri[6:]}.{local}"
            else:
                type_name = local
            fields.append((type_name, name))
        templated = inside_template or local in TEMPLATES
        for child in element:
            visit(child, templated)

    visit(root, False)
    namespace, _, class_name = cls.rpartition(".")
    lines = [
        "// Erzeugt von make-stubs.py",
        "#pragma warning disable CS0169, CS0649, CS8618",
        "using Microsoft.UI.Xaml;",
        "using Microsoft.UI.Xaml.Controls;",
        "using Microsoft.UI.Xaml.Shapes;",
        f"namespace {namespace};",
        f"partial class {class_name}",
        "{",
    ]
    for type_name, name in fields:
        lines.append(f"    private {type_name} {name};")
    lines.append("    public void InitializeComponent() { }")
    lines.append("}")
    (out / f"{class_name}.g.cs").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"{cls}: {len(fields)} Felder")
