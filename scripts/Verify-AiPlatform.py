"""Regression guard for personal model/machine assumptions in product copy."""
import os
from pathlib import Path
root=Path(__file__).resolve().parents[1]
files=['MainWindow.AiConnections.cs','MainWindow.ModelLibrary.cs','AiConnections.cs','ModelLibraryPresentation.cs']
for name in files:
 text=(root/'src/AiGym'/name).read_text(encoding='utf-8')
 # The current Windows user name stands in for any personal path; it is read at run time, never written here.
 for token in ['Qwen','BGE',os.environ.get('USERNAME','~no-user~'),'18090','18091','9931','5090']:
  assert token not in text,(name,token)
print(f'PASS no known personal-model or machine assumptions in {len(files)} product sources')
