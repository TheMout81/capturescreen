"""Vérifications XAML que le compilateur ne fait pas (erreurs qui n'apparaissent qu'au lancement).

1. Chaque {StaticResource X} doit être défini quelque part.
2. Chaque {DynamicResource X} doit exister dans les deux thèmes (clair et sombre).
3. Une liaison bidirectionnelle par défaut (IsChecked, Value, SelectedItem, Text d'un TextBox…)
   ne doit pas viser une propriété sans setter public : WPF lève une exception au chargement.

Usage : python tools/check_xaml.py   (code de sortie 1 en cas de problème)
"""
import glob
import re
import sys

# Sous Windows (CI GitHub), la sortie peut ne pas être en UTF-8.
sys.stdout.reconfigure(encoding="utf-8")

ROOT = "src/PureClean"
TWO_WAY = ["Text", "IsChecked", "SelectedItem", "SelectedValue", "SelectedIndex", "Value", "IsExpanded", "IsSelected", "IsOpen"]
TWO_WAY_TEXT_TAGS = ("<TextBox", "<Run", "<ComboBox")

# Chemins normalisés avec « / » : glob renvoie des « \\ » sous Windows.
xaml = {p.replace("\\", "/"): open(p, encoding="utf-8").read() for p in glob.glob(f"{ROOT}/**/*.xaml", recursive=True)}
code = "\n".join(open(p, encoding="utf-8").read() for p in glob.glob(f"{ROOT}/**/*.cs", recursive=True))
errors = []

defined = {k for s in xaml.values() for k in re.findall(r'x:Key="([^"]+)"', s)}
for path, text in xaml.items():
    for key in re.findall(r"\{StaticResource ([^}\s]+)\}", text):
        if key not in defined and not key.startswith("{x:Type"):
            errors.append(f"{path}: StaticResource « {key} » non définie")

themes = {name: set(re.findall(r'x:Key="([^"]+)"', xaml[f"{ROOT}/Resources/Themes/{name}.xaml"])) for name in ("Light", "Dark")}
for path, text in xaml.items():
    for key in set(re.findall(r"\{DynamicResource ([^}\s]+)\}", text)):
        for name, keys in themes.items():
            if key not in keys:
                errors.append(f"{path}: DynamicResource « {key} » absente du thème {name}")


def has_public_setter(prop: str) -> bool:
    block = re.search(r"public [^\n;=]*\b" + re.escape(prop) + r"\b\s*(\{[^}]*\}|\n\s*\{(?:[^{}]|\{[^{}]*\})*\})", code)
    if not block:
        return False
    body = block.group(1)
    return re.search(r"(?<!private )(?<!protected )(?<!init)\bset\b", body) is not None


for path, text in xaml.items():
    for attr in TWO_WAY:
        for match in re.finditer(r"(<\w[\w:.]*)[^<>]*?\s" + attr + r'="\{Binding ([^}]*)\}"', text):
            tag, binding = match.group(1), match.group(2)
            if "Mode=OneWay" in binding or "Mode=OneTime" in binding:
                continue
            if tag == "<Setter":
                # Le sens de la liaison dépend de la propriété visée par le Setter.
                target = re.search(r'Property="([^"]+)"', match.group(0))
                if not target or target.group(1) not in TWO_WAY or target.group(1) == "Text":
                    continue
            if attr == "Text" and tag not in TWO_WAY_TEXT_TAGS:
                continue
            prop = binding.split(",")[0].strip().removeprefix("Path=").split(".")[-1]
            if prop and not has_public_setter(prop):
                errors.append(f"{path}: {tag} {attr}={{Binding {binding}}} vise une propriété sans setter public (ajoutez Mode=OneWay)")

for error in errors:
    print(error)
print("XAML : aucun problème détecté." if not errors else f"XAML : {len(errors)} problème(s).")
sys.exit(1 if errors else 0)
