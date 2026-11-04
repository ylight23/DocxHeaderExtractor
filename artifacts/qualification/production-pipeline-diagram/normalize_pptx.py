import os
import sys
import tempfile
import zipfile
import xml.etree.ElementTree as ET


path = os.path.abspath(sys.argv[1])
types_ns = "http://schemas.openxmlformats.org/package/2006/content-types"
ET.register_namespace("", types_ns)
with zipfile.ZipFile(path, "r") as source:
    names = set(source.namelist())
    parts = [(item, source.read(item.filename)) for item in source.infolist()]

fd, temp_path = tempfile.mkstemp(suffix=".pptx", dir=os.path.dirname(path))
os.close(fd)
try:
    with zipfile.ZipFile(temp_path, "w", compression=zipfile.ZIP_DEFLATED) as target:
        for info, data in parts:
            if info.filename == "[Content_Types].xml":
                root = ET.fromstring(data)
                for override in list(root.findall(f"{{{types_ns}}}Override")):
                    part_name = override.get("PartName", "").lstrip("/")
                    if part_name and part_name not in names:
                        root.remove(override)
                data = ET.tostring(root, encoding="utf-8", xml_declaration=True)
            target.writestr(info, data)
    os.replace(temp_path, path)
finally:
    if os.path.exists(temp_path):
        os.remove(temp_path)
