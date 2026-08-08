# tools/compile_installer.py （仅负责调用 ISCC）
import subprocess
import sys
import os

def compile_installer(iss_path: str):
    iscc_path = r"C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    if not os.path.exists(iscc_path):
        raise RuntimeError(f"Inno Setup compiler not found at {iscc_path}")
    result = subprocess.run([iscc_path, iss_path, "/O+"], capture_output=True, text=True)
    if result.returncode != 0:
        print(result.stdout)
        print(result.stderr)
        raise RuntimeError(f"ISCC failed with code {result.returncode}")
    print("Inno Setup compilation completed.")

if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise RuntimeError("Usage: python compile_installer.py <installer.iss>")
    compile_installer(sys.argv[1])