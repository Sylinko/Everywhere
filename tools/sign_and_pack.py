import calendar
import concurrent.futures
import datetime
import os
import pyotp
import subprocess
import sys
import time

def timecode(for_time: datetime.datetime, interval: int) -> int:
    """Calculate the timecode for TOTP generation based on a specific interval."""
    if for_time.tzinfo:
        return int(calendar.timegm(for_time.utctimetuple())) % interval
    else:
        return int(time.mktime(for_time.timetuple())) % interval
    
def normalize_sha1(sha1: str) -> str:
    """Clean up the SHA1 fingerprint to retain only alphanumeric characters."""
    return ''.join(c for c in sha1 if c.isalnum())

def is_already_signed(target_path: str, signtool_exe: str) -> bool:
    """
    Verify if the file already has a valid digital signature.
    Uses '/pa' to specify the Default Authentication Verification Policy.
    """
    result = subprocess.run(
        [signtool_exe, 'verify', '/pa', target_path],
        capture_output=True,
        text=True,
        encoding='utf-8'
    )
    # Return code 0 means the file is successfully verified (already signed)
    return result.returncode == 0
    
def sign_worker(fingerprint: str, target_path: str, signtool_exe: str, max_retries: int = 3) -> str:
    """
    Worker function to sign a single file with verification and retry logic.
    Returns a status string indicating the result.
    """
    filename = os.path.basename(target_path)
    
    # Step 1: Check if the file is already signed
    if is_already_signed(target_path, signtool_exe):
        return f"[SKIP] {filename} is already signed."
    
    # Step 2: Attempt to sign the file with retries
    for attempt in range(max_retries):
        result = subprocess.run(
            [
                signtool_exe, 'sign', 
                '/sha1', fingerprint, 
                '/tr', 'http://time.certum.pl', 
                '/td', 'sha256', 
                '/fd', 'sha256', 
                '/v', target_path
            ],
            capture_output=True,
            text=True,
            encoding='utf-8'
        )

        # Check for success criteria
        if result.returncode == 0 and result.stdout.count("Error") == 0 and result.stderr.count("Error") == 0:
            return f"[SUCCESS] Signed: {filename}"
            
        # If failed and retries are left, wait briefly before retrying
        if attempt < max_retries - 1:
            time.sleep(2)
        else:
            raise RuntimeError(
                f"[ERROR] Failed to sign {filename} after {max_retries} attempts.\n"
                f"STDOUT:\n{result.stdout}\nSTDERR:\n{result.stderr}"
            )

def compile_installer(iss_path: str, fingerprint: str, signtool_exe: str) -> str:
    """Compile and sign the installer and its embedded uninstaller with Inno Setup."""
    configured_path = os.getenv('INNO_SETUP_COMPILER')
    candidates = [
        configured_path,
        r"C:\Program Files\Inno Setup 7\ISCC.exe",
        r"C:\Program Files (x86)\Inno Setup 7\ISCC.exe"
    ]
    iscc_path = next((path for path in candidates if path and os.path.isfile(path)), None)
    if not iscc_path:
        raise RuntimeError("Inno Setup 7 compiler not found. Set INNO_SETUP_COMPILER to the ISCC.exe path.")
    
    # Inno expands $q after parsing its command line; embedded quotes are not CRT-escaped.
    sign_command = (
        f'$q{signtool_exe}$q sign /sha1 {fingerprint} '
        '/tr http://time.certum.pl /td sha256 /fd sha256 /v $f'
    )
    result = subprocess.run(
        [
            iscc_path,
            "--output=yes",
            f"--signtool=EverywhereSign={sign_command}",
            iss_path,
        ],
        capture_output=True,
        text=True,
        encoding='utf-8'
    )
    
    if result.returncode != 0:
        print(f"ISCC STDOUT:\n{result.stdout}")
        print(f"ISCC STDERR:\n{result.stderr}")
        raise RuntimeError(f"Inno Setup compilation failed with exit code {result.returncode}")
    
    version = os.getenv('VERSION')
    if not version:
        raise RuntimeError("VERSION is required to locate the compiled installer.")

    installer_path = os.path.abspath(
        os.path.join(os.path.dirname(iss_path), '..', f'Everywhere-Windows-x64-Setup-v{version}.exe')
    )
    if not os.path.isfile(installer_path):
        raise RuntimeError(f"Compiled installer was not found at: {installer_path}")
    if not is_already_signed(installer_path, signtool_exe):
        raise RuntimeError("Inno Setup completed, but the generated installer signature could not be verified.")

    print("Inno Setup compiled and signed the installer and embedded uninstaller successfully.")
    return installer_path
    
if __name__ == "__main__":
    # Retrieve credentials from environment variables
    username = os.getenv('SIGN_USERNAME')
    otp_token = os.getenv('SIGN_OTP_TOKEN')
    
    if not username or not otp_token:
        raise RuntimeError("SIGN_USERNAME or SIGN_OTP_TOKEN environment variables are missing.")
    
    # Parse command line arguments
    publish_path = None
    iss_path = None
    
    for arg in sys.argv[1:]:
        if arg.lower().endswith('.iss'):
            iss_path = arg
        else:
            publish_path = arg
    
    if not publish_path:
        raise RuntimeError("Usage: python sign.py <publish_path> [<installer.iss>]")
    
    print(f"Starting signing workflow targeting: {publish_path}")
    if iss_path:
        print(f"Installer configuration found: {iss_path}")

    # Resolve signtool path
    sign_windows_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'sign_windows')
    signtool_exe = os.path.join(sign_windows_dir, 'signtool.exe')
    
    if not os.path.exists(signtool_exe):
        # Fallback to extensionless name if .exe is missing
        signtool_exe = os.path.join(sign_windows_dir, 'signtool')

    # Initialize TOTP generator
    totp = pyotp.TOTP(otp_token, digest='SHA256', issuer='Certum')

    # Wait for the optimal time window to log in to Certum
    print("\nAuthenticating with SimplySignDesktop...")
    while True:
        if not (3 < timecode(datetime.datetime.now(), 30) < 13):
            time.sleep(0.1)
            continue
        break

    current_otp = totp.now()
    creation_flags = 0x08000000 # CREATE_NO_WINDOW

    # Launch SimplySignDesktop to expose the virtual smart card
    proc = subprocess.Popen(
        [r'C:\Program Files\Certum\SimplySign Desktop\SimplySignDesktop.exe', '/autologin', username, str(current_otp)], 
        stdout=subprocess.PIPE, 
        stderr=subprocess.STDOUT,
        text=True,
        encoding='utf-8',
        creationflags=creation_flags
    )

    fingerprint: str = None
    
    try:
        # Read the fingerprint output from the application
        while True:
            line = proc.stdout.readline()
            if not line:
                break

            fingerprint = normalize_sha1(line.strip())
            break

        if not fingerprint:
            raise RuntimeError("No fingerprint received from SimplySignDesktop.exe. Authentication may have failed.")

        print(f"Successfully obtained certificate fingerprint: {fingerprint}")

        # Phase 1: Sign all executables and libraries concurrently
        print(f"\nPhase 1: Scanning and signing binaries in '{publish_path}'")
        
        files_to_process = [
            os.path.join(publish_path, f)
            for f in os.listdir(publish_path)
            if f.lower().endswith(('.exe', '.dll'))
        ]
        
        # Determine optimal thread count to prevent API rate limiting
        max_workers = 6 
        
        # Execute concurrent signing tasks
        with concurrent.futures.ThreadPoolExecutor(max_workers=max_workers) as executor:
            future_to_path = {
                executor.submit(sign_worker, fingerprint, path, signtool_exe): path 
                for path in files_to_process
            }
            
            for future in concurrent.futures.as_completed(future_to_path):
                path = future_to_path[future]
                try:
                    result_msg = future.result()
                    print(result_msg)
                except Exception as exc:
                    print(f"\n[FATAL] Unhandled exception processing {os.path.basename(path)}: {exc}")
                    raise
        
        # Phase 2: Inno signs both the installer and its embedded uninstaller.
        if iss_path:
            print(f"\nPhase 2: Compiling and signing installer from script: {iss_path}")
            installer_path = compile_installer(iss_path, fingerprint, signtool_exe)
            print(f"[SUCCESS] Signed installer: {os.path.basename(installer_path)}")
        
        print("\n=== Workflow Completed Successfully ===")
        
    finally:
        # Ensure the background SimplySign process is terminated
        proc.terminate()