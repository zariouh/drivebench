import os, glob, json, re, time
from google import genai

client = genai.Client(api_key=os.environ['GEMINI_API_KEY'])
prompt = os.environ['USER_PROMPT']

code_files = glob.glob('**/*.cs', recursive=True) + glob.glob('**/*.kt', recursive=True)
context = ''
for filepath in code_files:
    if '.github' not in filepath and 'build' not in filepath:
        with open(filepath, 'r', encoding='utf-8', errors='ignore') as f:
            context += f'\n--- FILE: {filepath} ---\n' + f.read()

full_prompt = f"""You are an expert C# and Kotlin developer modifying DriveBench.
Instructions: {prompt}

Return ONLY a JSON mapping of updated relative file paths to their complete updated source text.
Format: {{"path/to/file": "file content..."}}

Existing Codebase:
{context}"""

response = None
for attempt in range(3):
    try:
        response = client.models.generate_content(
            model='gemini-3.8-flash',
            contents=full_prompt
        )
        break
    except Exception as e:
        print(f"Attempt {attempt+1} failed: {e}. Retrying in 5 seconds...")
        time.sleep(5)

if not response:
    raise Exception("Failed to get response from Gemini after 3 attempts.")

raw = response.text.strip()
if raw.startswith('```'):
    raw = re.sub(r'^```[a-z]*\n|```$', '', raw, flags=re.MULTILINE).strip()

try:
    files = json.loads(raw)
    for path, content in files.items():
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, 'w', encoding='utf-8') as f:
            f.write(content)
        print(f'Updated {path}')
except Exception as e:
    print(f'Failed to parse AI output: {e}\nRaw Output:\n{raw}')
