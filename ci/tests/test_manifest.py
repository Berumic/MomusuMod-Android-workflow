import importlib.util
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location("rewrite_manifest", Path(__file__).parents[1] / "Rewrite-Manifest.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
A = module.A


class ManifestTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "res" / "values").mkdir(parents=True)
        (self.root / "smali" / "old").mkdir(parents=True)
        (self.root / "AndroidManifest.xml").write_text("""<manifest xmlns:android="http://schemas.android.com/apk/res/android" package="old.game" android:sharedUserId="old.shared">
        <permission android:name="old.game.PRIVATE" android:protectionLevel="signature"/>
        <uses-permission android:name="old.game.PRIVATE"/>
        <uses-permission android:name="android.permission.INTERNET"/>
        <queries><package android:name="com.dmm.app.store"/></queries>
        <application android:name=".App" android:label="@string/app_name">
          <provider android:name="Provider" android:authorities="old.game.files;vendor.unique" android:permission="old.game.PRIVATE"/>
          <activity android:name=".Main" android:parentActivityName=".Parent" android:taskAffinity="old.game.task">
            <intent-filter><category android:name="android.intent.category.LAUNCHER"/>
              <data android:scheme="old.game"/></intent-filter>
          </activity>
          <activity-alias android:name=".Alias" android:targetActivity=".Main"/>
          <service android:name="vendor.Service" android:process=":local"/>
        </application></manifest>""", encoding="utf-8")
        (self.root / "res/values/strings.xml").write_text('<resources><string name="authority">old.game.files</string></resources>', encoding="utf-8")
        (self.root / "smali/old/Provider.smali").write_text(
            '.class public Lold/game/Provider;\nconst-string v0, "old.game.files"\nconst-string v1, "old.game.PRIVATE"\nconst-string v2, "old.game"\n',
            encoding="utf-8")

    def test_identity_and_references(self):
        result = module.rewrite(self.root, "old.game", "old.game.mod", "Game Mod")
        root = ET.parse(self.root / "AndroidManifest.xml").getroot()
        self.assertEqual(root.get("package"), "old.game.mod")
        self.assertNotIn(A + "sharedUserId", root.attrib)
        app = root.find("application")
        self.assertEqual(app.get(A + "name"), "old.game.App")
        self.assertEqual(app.get(A + "label"), "Game Mod")
        self.assertEqual(app.find("activity").get(A + "name"), "old.game.Main")
        self.assertEqual(app.find("activity-alias").get(A + "targetActivity"), "old.game.Main")
        self.assertEqual(app.find("provider").get(A + "authorities"), "old.game.mod.files;old.game.mod.vendor.unique")
        self.assertEqual(root.find("permission").get(A + "name"), "old.game.mod.PRIVATE")
        self.assertEqual(root.findall("uses-permission")[0].get(A + "name"), "old.game.mod.PRIVATE")
        self.assertEqual(root.findall("uses-permission")[1].get(A + "name"), "android.permission.INTERNET")
        self.assertEqual(app.find("activity").get(A + "taskAffinity"), "old.game.mod.task")
        self.assertEqual(app.find("service").get(A + "process"), ":local")
        self.assertEqual(root.find("./queries/package").get(A + "name"), "com.dmm.app.store")
        self.assertEqual(app.find(".//data").get(A + "scheme"), "old.game")
        smali = (self.root / "smali/old/Provider.smali").read_text()
        self.assertIn('Lold/game/Provider;', smali)
        self.assertIn('"old.game.mod.files"', smali)
        self.assertIn('"old.game.mod.PRIVATE"', smali)
        self.assertIn('"old.game"', smali)
        self.assertEqual(ET.parse(self.root / "res/values/strings.xml").find("string").text, "old.game.mod.files")
        self.assertEqual(result["smaliFilesUpdated"], 1)

    def test_reject_wrong_source(self):
        with self.assertRaises(ValueError):
            module.rewrite(self.root, "wrong.game", "new.game", "Mod")

    def test_reject_same_identity(self):
        with self.assertRaises(ValueError):
            module.rewrite(self.root, "old.game", "old.game", "Mod")

    def test_reject_unresolved_authority(self):
        path = self.root / "AndroidManifest.xml"
        path.write_text(path.read_text().replace("old.game.files;vendor.unique", "@string/authority"))
        with self.assertRaises(ValueError):
            module.rewrite(self.root, "old.game", "old.game.mod", "Mod")


if __name__ == "__main__":
    unittest.main()

