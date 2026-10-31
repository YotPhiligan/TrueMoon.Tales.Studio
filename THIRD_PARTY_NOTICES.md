# Third-party notices

The original Studio software is licensed under Apache-2.0; see [LICENSE](LICENSE)
and [NOTICE](NOTICE). Third-party material retains its own license and attribution.

## Comfy Org workflow templates

The following bundled workflow JSON files contain upstream documentation and
template material identifying the Comfy Org `workflow_templates` project:

- `Workflows/Omni.v2/Templates/Native.ui.json`
- `Workflows/TemplateData/Clair/Workflows/CL-MC01.A.Omni.v1.json`
- `Workflows/TemplateData/Clair/Workflows/CL-MC01.B.Context.Omni.v1.json`
- `Workflows/TemplateData/Clair/Workflows/CL-ST01.OmniFace.v1.json`

Upstream: [Comfy-Org/workflow_templates](https://github.com/Comfy-Org/workflow_templates).
License: MIT. Copyright (c) 2023-present Comfy Org.
The upstream copyright and permission notice is retained in
[ThirdParty/ComfyWorkflowTemplates.LICENSE](ThirdParty/ComfyWorkflowTemplates.LICENSE).
The retained license text was retrieved at upstream revision `c067929d7e9cc0f3fd15be348fa1a1d85da15946` on
2026-11-01; this identifies the license snapshot, not the original revision of
every bundled workflow. Studio-specific additions are covered by Apache-2.0;
that grant does not replace the upstream MIT notice.

Bundled JSON, including their embedded upstream notes and the SHA records in
`Workflows/TemplateData/Provenance.json`, are preserved without edits.

## External runtime and project content

ComfyUI, its custom nodes, model weights, FFmpeg, PyAV, Pillow, psutil, NumPy,
imageio-ffmpeg and system fonts are separate runtime components. Their source,
binaries, weights and font files are not relicensed by Studio's Apache license.
When distributing a runtime or bundled application, retain the applicable
component licenses and notices for the exact versions/builds shipped.
This repository's license setup is not a complete audit of every optional runtime.

Literary texts, lore, character artwork, music, generated media and the underlying
creative content visible in Verification screenshots or examples are outside the
Studio software grant. Their separate rights and terms continue to apply; see
the scope statement in [README](README.md#лицензия).
