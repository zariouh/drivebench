import os, glob, json, re, time
from google import genai

client = genai.Client(api_key=os.environ['GEMINI_API_KEY'])

# Read instructions directly from AI_COMMAND.txt
command_file = 'AI_COMMAND.txt'
if not os.path.exists(command_file):
    raise Exception(f"Missing {command_file}! Please create this file in the repo root with your instructions.")

with open(command_file, 'r', encoding='utf-8') as f:
    prompt = f.read().strip()

if not prompt:
    raise Exception(f"{command_file} is empty! Please write your prompt inside the file.")

print(f"Executing AI Command from {command_file}:\n{prompt}\n")

code_files = glob.glob('**/*.cs', recursive=True) + glob.glob('**/*.kt', recursive=True)
context = ''
for filepath in code_files:
    if '.github' not in filepath and 'build' not in filepath and '.gradle' not in filepath:
        with open(filepath, 'r', encoding='utf-8', errors='ignore') as f:
            context += f'\n--- FILE: {filepath} ---\n' + f.read()

full_prompt = f"""You are an expert C# and Kotlin developer modifying DriveBench.
Instructions: {prompt}

Return ONLY a JSON mapping of updated relative file paths to their complete updated source text.
Format: {{"path/to/file": "file content..."}}

Existing Codebase:
{context}"""

# Fallback model list directly matched to your available Google AI Studio models
models_to_try = [
    'gemini-3.8-flash',
    'gemini-3.6-flash',
    'gemini-3.5-flash-lite',
    'gemini-3.1-flash-lite',
    'gemini-3-flash-preview'
]

response = None
for model_name in models_to_try:
    print(f"Attempting code generation with model: {model_name}...")
    for attempt in range(2):
        try:
            response = client.models.generate_content(
                model=model_name,
                contents=full_prompt
            )
            print(f"Successfully generated response using {model_name}.")
            break
        except Exception as e:
            print(f"[{model_name}] Attempt {attempt+1} failed: {e}")
            time.sleep(3)
    if response:
        break

if not response:
    raise Exception("Failed to get a valid response from any available Gemini model after multiple retries.")

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
