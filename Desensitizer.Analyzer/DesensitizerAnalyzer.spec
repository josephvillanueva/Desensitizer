# -*- mode: python ; coding: utf-8 -*-
# Lean onedir build of Desensitizer.Analyzer/analyzer.py (Presidio + spaCy). Used by tools/Build-Analyzers.ps1.
# torch, transformers and the other generative-model stacks are excluded: this worker never imports them,
# and letting PyInstaller analyse them is what made the earlier build crawl.
from PyInstaller.utils.hooks import collect_all, collect_data_files

datas, binaries, hiddenimports = [], [], []
for package in ('presidio_analyzer', 'en_core_web_sm', 'phonenumbers', 'tldextract'):
    d, b, h = collect_all(package)
    datas += d; binaries += b; hiddenimports += h
# spaCy: data files and compiled extensions only; submodules are pulled in by import analysis.
datas += collect_data_files('spacy')
datas += collect_data_files('thinc')
hiddenimports += ['spacy.lang.en', 'thinc.backends.numpy_ops', 'thinc.layers', 'blis', 'cymem', 'preshed', 'murmurhash', 'srsly']

EXCLUDES = [
    'torch', 'torchgen', 'functorch', 'transformers', 'gliner', 'onnxruntime', 'accelerate',
    'sympy', 'mpmath', 'networkx', 'huggingface_hub', 'safetensors', 'sentencepiece', 'tokenizers',
    'tensorflow', 'jax', 'cupy', 'matplotlib', 'pandas', 'scipy', 'IPython', 'notebook', 'pytest',
    'tkinter', 'PyInstaller',
]

a = Analysis(
    ['analyzer.py'],
    pathex=[], binaries=binaries, datas=datas, hiddenimports=hiddenimports,
    hookspath=[], hooksconfig={}, runtime_hooks=[], excludes=EXCLUDES, noarchive=False, optimize=0,
)
pyz = PYZ(a.pure)
exe = EXE(pyz, a.scripts, [], exclude_binaries=True, name='DesensitizerAnalyzer', debug=False,
          bootloader_ignore_signals=False, strip=False, upx=False, console=True,
          disable_windowed_traceback=False, argv_emulation=False, target_arch=None,
          codesign_identity=None, entitlements_file=None)
coll = COLLECT(exe, a.binaries, a.datas, strip=False, upx=False, upx_exclude=[], name='DesensitizerAnalyzer')

