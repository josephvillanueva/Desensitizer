# -*- mode: python ; coding: utf-8 -*-
# Lean onedir build of Desensitizer.Analyzer/privacy_filter_analyzer.py. Used by tools/Build-Analyzers.ps1.
from PyInstaller.utils.hooks import collect_all, collect_data_files
datas, binaries, hiddenimports = [], [], []
for package in ('onnxruntime', 'tokenizers'):
    d, b, h = collect_all(package)
    datas += d; binaries += b; hiddenimports += h
EXCLUDES = ['torch', 'torchgen', 'functorch', 'transformers', 'gliner', 'spacy', 'thinc', 'presidio_analyzer', 'accelerate', 'sympy', 'networkx', 'huggingface_hub', 'safetensors', 'tensorflow', 'jax', 'matplotlib', 'pandas', 'scipy', 'IPython', 'pytest', 'tkinter', 'PyInstaller']
a = Analysis(
    ['privacy_filter_analyzer.py'],
    pathex=[], binaries=binaries, datas=datas, hiddenimports=hiddenimports,
    hookspath=[], hooksconfig={}, runtime_hooks=[], excludes=EXCLUDES, noarchive=False, optimize=0,
)
pyz = PYZ(a.pure)
exe = EXE(pyz, a.scripts, [], exclude_binaries=True, name='PrivacyFilterAnalyzer', debug=False,
          bootloader_ignore_signals=False, strip=False, upx=False, console=True,
          disable_windowed_traceback=False, argv_emulation=False, target_arch=None,
          codesign_identity=None, entitlements_file=None)
coll = COLLECT(exe, a.binaries, a.datas, strip=False, upx=False, upx_exclude=[], name='PrivacyFilterAnalyzer')
