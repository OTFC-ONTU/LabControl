#!/usr/bin/env python3
"""Builds ../prototype/index.html (self-contained) from style.css, i18n.js, app.js, icons.json and logo.txt."""
import os, subprocess, sys
H = os.path.dirname(os.path.abspath(__file__))
subprocess.run([sys.executable, os.path.join(H, 'icons.py')], check=True)
subprocess.run([sys.executable, os.path.join(H, 'appicons.py')], check=True)
r = lambda n: open(os.path.join(H, n), encoding='utf-8').read()
page = f'''<!doctype html>
<html lang="uk" data-theme="light"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>LabControl Redesign Prototype</title>
<link rel="icon" href="{r('logo.txt')}">
<style>{r('style.css')}</style></head>
<body><div id="strip"></div><div id="stage"><div id="scaler"><div id="win" class="win"></div></div></div>
<script>
const ICONS = {r('icons.json')};
const LOGO = "{r('logo.txt')}";
{r('i18n.js')}
{r('app.js')}
</script></body></html>'''
os.makedirs(os.path.join(H, '..', 'prototype'), exist_ok=True)
open(os.path.join(H, '..', 'prototype', 'index.html'), 'w', encoding='utf-8').write(page)
print('prototype', len(page)//1024, 'KB')
