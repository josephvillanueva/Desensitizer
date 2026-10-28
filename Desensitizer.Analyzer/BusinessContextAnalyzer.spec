# -*- mode: python ; coding: utf-8 -*-
# Lean onedir build of Desensitizer.Analyzer/business_context_analyzer.py. Used by tools/Build-Analyzers.ps1.
from PyInstaller.utils.hooks import collect_all, collect_data_files
datas, binaries, hiddenimports = [], [], []
for package in ('gliner', 'onnxruntime', 'tokenizers', 'sentencepiece', 'safetensors'):
    d, b, h = collect_all(package)
    datas += d; binaries += b; hiddenimports += h
datas += collect_data_files('transformers')
hiddenimports += ['transformers.models.deberta_v2', 'transformers.models.deberta_v2.tokenization_deberta_v2', 'transformers.models.deberta_v2.modeling_deberta_v2']
EXCLUDES = ['tensorflow', 'jax', 'flax', 'matplotlib', 'pandas', 'scipy', 'IPython', 'notebook', 'pytest', 'tkinter', 'PyInstaller', 'presidio_analyzer', 'spacy', 'thinc']
a = Analysis(
    ['business_context_analyzer.py'],
    pathex=[], binaries=binaries, datas=datas, hiddenimports=hiddenimports,
    hookspath=[], hooksconfig={}, runtime_hooks=[], excludes=EXCLUDES, noarchive=False, optimize=0,
)
pyz = PYZ(a.pure)
exe = EXE(pyz, a.scripts, [], exclude_binaries=True, name='BusinessContextAnalyzer', debug=False,
          bootloader_ignore_signals=False, strip=False, upx=False, console=True,
          disable_windowed_traceback=False, argv_emulation=False, target_arch=None,
          codesign_identity=None, entitlements_file=None)
coll = COLLECT(exe, a.binaries, a.datas, strip=False, upx=False, upx_exclude=[], name='BusinessContextAnalyzer')
