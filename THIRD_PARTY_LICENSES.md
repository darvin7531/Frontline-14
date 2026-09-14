# Frontline 14 — Third-Party Licenses and Attribution

This document tracks upstream and third-party material used by **Frontline 14**.

It is an attribution and licensing index, not a replacement for the original license texts, file-level metadata, copyright notices, or source-specific requirements.

## Currently inherited / included

### Space Station 14 / Space Wizards Federation

- Source: https://github.com/space-wizards/space-station-14
- Role: upstream game/content code and systems
- Primary code license: MIT
- Notes: individual assets may use separate Creative Commons or other licenses and must be checked through their metadata.

### Space Syndicate / Corvax

- Source: https://github.com/space-syndicate/space-station-14
- Role: Russian-language SS14 codebase and localization used as the direct starting point for Frontline 14
- Primary code license: MIT
- Notes: individual assets may use separate licenses. The original MIT license text remains preserved in `LICENSE.TXT`.

### RobustToolbox

- Source: https://github.com/space-wizards/RobustToolbox
- Role: engine / framework
- License: see RobustToolbox's own repository and legal files
- Notes: RobustToolbox contains its own historical and third-party licensing information. Its license terms are not replaced by Frontline 14 licensing.

## Approved source class for future imports

Frontline 14 may incorporate material from other projects when the specific material is legally compatible with the project.

Examples of source projects that may be evaluated include:

### RMC-14

- Source: https://github.com/RMC-14/RMC-14
- Potential role: selected gameplay systems or implementation ideas/code
- RMC-14 specific code license: MIT
- Status: **not automatically considered incorporated into Frontline 14**. Add a concrete attribution entry when code or assets are actually imported.
- Important: RMC-14 assets may use CC-BY-SA, CC-BY-NC-SA or other asset-specific licenses; check metadata before importing any asset.

Other MIT, BSD, Apache or similarly permissive projects may also be evaluated case by case.

## Import record format

When a concrete third-party implementation is imported into Frontline 14, add an entry containing at least:

- project / author;
- source repository or canonical source;
- original file(s), commit, PR, tag or other traceable revision where practical;
- imported Frontline 14 file(s) or subsystem;
- applicable license;
- required copyright notice / attribution;
- whether the code was copied, adapted, partially rewritten or only used as a reference;
- asset metadata if any assets are involved.

Suggested format:

```text
### <Project / Component>
- Source: <URL>
- Revision: <commit / PR / tag>
- Imported into: <paths or subsystem>
- License: <license>
- Copyright: <notice>
- Method: copied / adapted / rewritten from / referenced
- Notes: <additional requirements>
```

## Rules

1. A repository-level code license does not automatically cover all assets in that repository.
2. File-level license notices and asset metadata take precedence over assumptions based on the repository's main license.
3. Required MIT/BSD/Apache copyright and permission notices must be preserved where applicable.
4. Creative Commons attribution and ShareAlike requirements must be followed where applicable.
5. Material with unclear provenance must not be imported until its licensing is resolved.
6. Proprietary, leaked, non-redistributable, or otherwise incompatible material must not be imported without explicit permission from the relevant rights holder and approval from the Frontline 14 project owner.
7. Frontline 14's proprietary license applies only to original Frontline 14 material and copyrightable Frontline 14 additions; it does not erase third-party rights.
