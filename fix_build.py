import os, glob, json, re, time
from google import genai

client = genai.Client(api_key=os.environ['GEMINI_API_KEY'])

error_log = ""
if os.path.exists("build_error.log"):
    with open("build_error.log", "r", encoding="utf-8", errors="ignore") as f:
        error_log = f.read()

code_files = glob.glob('**/*.cs', recursive=True) + glob.glob('**/*.csproj', recursive=True)
context = ''
for filepath in code_files:
    if '.github' not in filepath and 'bin' not in filepath and 'obj' not in filepath:
        with open(filepath, 'r', encoding='utf-8', errors='ignore') as f:
            context += f'\n--- FILE: {filepath} ---\n' + f.read()

full_prompt = f"""You are an automated C#/.NET build fixing agent.
The Windows build failed with the following error output:

=== BUILD ERROR LOG ===
{error_log}
=======================

Fix all compilation errors in the source code (e.g., missing imports, type mismatches).
Return ONLY a JSON mapping of updated relative file paths to their complete updated source text.
Format: {{"path/to/file": "file content..."}}

Existing Codebase:
{context}"""

models_to_try = [
    'gemini-3.8-flash',
    'gemini-3.6-flash',
    'gemini-3.5-flash-lite',
    'gemini-3.1-flash-lite',
    'gemini-3-flash-preview'
]

response = None
for model_name in models_to_try:
    try:
        response = client.models.generate_content(model=model_name, contents=full_prompt)
        if response:
            break
    except Exception:
        time.sleep(2)

if response:
    raw = response.text.strip()
    if raw.startswith('```'):
        raw = re.sub(r'^```[a-z]*\n|```$', '', raw, flags=re.MULTILINE).strip()
    
    try:
        files = json.loads(raw)
        for path, content in files.items():
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, 'w', encoding='utf-8') as f:
                f.write(content)
        print("Applied AI auto-fix to Windows codebase.")
    except Exception as e:
        print(f"Error parsing AI response: {e}")
