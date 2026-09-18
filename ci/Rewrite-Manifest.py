"""Rewrite installation identity without renaming Java classes or DMM login schemes."""
import argparse
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

ANDROID = "http://schemas.android.com/apk/res/android"
A = "{" + ANDROID + "}"
ET.register_namespace("android", ANDROID)


def rewrite(directory, source_package, mod_package, label):
    directory = Path(directory)
    manifest = directory / "AndroidManifest.xml"
    tree = ET.parse(manifest)
    root = tree.getroot()
    if root.get("package") != source_package:
        raise ValueError("Decoded manifest does not match source package")
    if source_package == mod_package or not re.fullmatch(r"[a-zA-Z]\w*(?:\.[a-zA-Z]\w*)+", mod_package):
        raise ValueError("Invalid independent package name")
    mapping = {}
    providers = []
    permissions = []
    def relocated(value):
        if value == source_package or value.startswith(source_package + "."):
            return mod_package + value[len(source_package):]
        return mod_package + "." + value

    # Relative component names resolve against the OLD package, not the clone.
    class_attrs = {
        "application": ("name", "backupAgent", "appComponentFactory", "manageSpaceActivity", "zygotePreloadName"),
        "activity": ("name", "parentActivityName"),
        "activity-alias": ("name", "targetActivity", "parentActivityName"),
        "service": ("name",), "receiver": ("name",), "provider": ("name",),
        "instrumentation": ("name",),
    }
    for element in root.iter():
        for attr in class_attrs.get(element.tag, ()):
            value = element.get(A + attr)
            if value and (value.startswith(".") or "." not in value):
                element.set(A + attr, source_package + ("" if value.startswith(".") else ".") + value)
        if element.tag == "provider":
            authority = element.get(A + "authorities", "")
            for value in authority.split(";"):
                if not value or value.startswith("@"):
                    raise ValueError("Provider authorities must be literal nonempty strings")
                mapping[value] = relocated(value)
                providers.append(value)
        if element.tag in ("permission", "permission-group", "permission-tree"):
            value = element.get(A + "name", "")
            if not value or value.startswith("@"):
                raise ValueError("Custom permission must have a literal name")
            mapping[value] = relocated(value)
            permissions.append(value)

    for element in root.iter():
        for key, value in list(element.attrib.items()):
            if key == A + "authorities":
                element.set(key, ";".join(mapping[v] for v in value.split(";")))
            elif value in mapping:
                element.set(key, mapping[value])
            elif key in (A + "taskAffinity", A + "process") and value and not value.startswith(":"):
                element.set(key, relocated(value))
            elif element.tag == "instrumentation" and key == A + "targetPackage" and value == source_package:
                element.set(key, mod_package)
    root.set("package", mod_package)
    root.attrib.pop(A + "sharedUserId", None)
    root.attrib.pop(A + "sharedUserLabel", None)
    app = root.find("application")
    if app is None:
        raise ValueError("Missing application")
    app.set(A + "label", label)
    for element in app:
        categories = element.findall("./intent-filter/category")
        if any(c.get(A + "name") in ("android.intent.category.LAUNCHER", "android.intent.category.LEANBACK_LAUNCHER") for c in categories):
            element.set(A + "label", label)
    tree.write(manifest, encoding="utf-8", xml_declaration=True)

    # Keep exact authority/permission literals consistent in resources and DEX.
    # Smali is produced and assembled by Apktool; class descriptors are untouched.
    replacements = 0
    for path in directory.glob("smali*/**/*.smali"):
        content = path.read_text(encoding="utf-8")
        updated = content
        for old, new in mapping.items():
            updated = updated.replace('"' + old + '"', '"' + new + '"')
        if updated != content:
            path.write_text(updated, encoding="utf-8")
            replacements += 1
    for path in (directory / "res").rglob("*.xml"):
        resource = ET.parse(path)
        changed = False
        for element in resource.getroot().iter():
            if element.text in mapping:
                element.text = mapping[element.text]
                changed = True
            for key, value in list(element.attrib.items()):
                if value in mapping:
                    element.set(key, mapping[value])
                    changed = True
        if changed:
            resource.write(path, encoding="utf-8", xml_declaration=True)
    return {
        "sourcePackage": source_package, "modPackage": mod_package,
        "providerAuthorities": {p: mapping[p] for p in providers},
        "customPermissions": {p: mapping[p] for p in permissions},
        "smaliFilesUpdated": replacements,
        "loginSchemes": "preserved; device login validation required",
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--directory", required=True)
    parser.add_argument("--source-package", required=True)
    parser.add_argument("--mod-package", required=True)
    parser.add_argument("--label", required=True)
    parser.add_argument("--report", required=True)
    args = parser.parse_args()
    result = rewrite(args.directory, args.source_package, args.mod_package, args.label)
    Path(args.report).write_text(json.dumps(result, indent=2), encoding="utf-8")

