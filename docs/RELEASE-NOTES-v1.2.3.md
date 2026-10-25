# Release notes v1.2.3 — The Beast Roofer Edition (EN + FR)

_Draft for the Gitea release page. Bilingual per fork rule. No internal jargon._

---

## English

### Improved: cloud and sky ghosting with DLSS — now also with native-resolution 4K mods

Two improvements in one:

- **Native-resolution configurations** (emulator resolution 1x + an in-engine 4K render
  mod, e.g. TOTK Optimizer at 3840x2160): the DLSS motion-quality system was **silently
  inactive** in this configuration — clouds and sky ghosted exactly as raw DLSS. It now
  engages in every resolution configuration, scaled or native.
- **Camera zooms** (aiming a bow, spyglass): the anti-transition protection used to mute
  the sky handling during every zoom, letting clouds ghost briefly in broad daylight. It
  now only triggers on real scene cuts (teleports, shrine transitions), never on zooms.

**Known behavior:** during roughly the first minute after loading a save, some cloud
ghosting can appear, fade, and come back while the system arms itself (it has to find the
game's camera, its depth buffer, and build up motion history). It then settles for the
rest of the session. This release is an **improvement, not perfection** — brief cloud
ghosting can still occur in specific moments.

### Improved: black lines / dark frame echo during camera movement

The recurring "thin black frame" some players saw while panning the camera had a real
mechanism behind it: the game leaves a 2-pixel black border on its rendered image at
scaled resolutions, and the DLSS history smears that border inward during camera motion —
a dotted echo proportional to pan speed. The border is now repaired before DLSS ever sees
the image. As a side effect, cloud trails that were fed by the same polluted history are
also reduced.

### Fixed: crash when toggling fullscreen (F11) or resizing the window with DLSS active

Toggling fullscreen or resizing while DLSS ran could crash the emulator (GPU errors on the
swapchain recreation path). The recreation sequence was rebuilt — the old chain and its
views now retire only once the display is done with them, and the acquire loop follows the
Vulkan rules to the letter. Validated with hundreds of consecutive fullscreen toggles.

### New: Frame Generation self-recovery

Frame Generation could silently stop injecting frames (display back to 1x, no error
anywhere) until a fullscreen toggle happened to revive it. A watchdog now detects that
state and applies the same remedy automatically within ~8 seconds. If you ever need to
disable it: set the environment variable `RYUJINX_DLSS_FG_WATCHDOG=0`.

### Fixed: game list could come up empty after a crash

If the emulator was killed or crashed at the exact moment it was saving its configuration,
the config file could be truncated — and your game directories list came up empty on the
next launch. Configuration files are now written atomically: a crash can no longer damage
them.

### Fixed: changing DLSS settings could restart with a partial state

Changing certain DLSS settings restarts the emulator; the restart could come back up with
only part of the motion-quality profile applied (cloud ghosting returning, border fix
lost). The full profile is now re-derived from scratch on every restart.

### Fixed: texture corruption with "Force mipmaps" + native-resolution 4K mods

With the (off by default) *Force mipmaps* option enabled **and** an in-engine 4K render
mod at native emulator resolution, procedural textures (shrine portals, some terrain)
could show purple/rainbow corruption, most often around shrine transitions. Cause: mip
levels were being generated behind small render targets the game redraws every frame. Such
textures are now excluded automatically the moment the game renders into them. Art
textures keep the full benefit of the option.

### Better diagnostics

When the DLSS motion-quality system cannot run, it now says why in the log ("camera not
paired", "depth not paired"...) instead of staying silent. If you report an issue, your
log will tell us much more than before.

### Reminder — hardware requirements for DLSS

A lot of reports boil down to this, so let's be explicit:

- **DLSS and DLAA require an NVIDIA RTX GPU** (RTX 20 series or newer). They **cannot**
  work on GTX cards (10/16 series) — the hardware simply lacks the units DLSS runs on.
  This is an NVIDIA limitation, not something the fork can patch around.
- **Frame Generation requires an RTX 40 series or newer.**
- **No RTX card? Use NIS** (NVIDIA Image Scaling, included in this fork): it runs on any
  GPU and remains the recommended upscaler for GTX and non-NVIDIA cards.
- A small number of RTX configurations still crash when DLSS activates; this is under
  active investigation — see the pinned diagnostic-build ticket if you are affected.

### Testing scope

This release was built and validated on a single machine — Zelda TOTK and BOTW as main
test games, both scaled and native-4K-mod configurations, plus a package-level test of
the exact archives published here. Other
GPUs, drivers and games may behave differently: if something regresses for you, open an
issue with your log and it becomes tomorrow's fix.

---

## Français

### Amélioré : ghosting des nuages et du ciel avec DLSS — maintenant aussi avec les mods 4K en résolution native

Deux améliorations en une :

- **Configurations en résolution native** (résolution émulateur 1x + un mod de rendu 4K
  in-engine, ex. TOTK Optimizer en 3840x2160) : le système de qualité de mouvement DLSS
  était **silencieusement inactif** dans cette configuration — les nuages et le ciel
  ghostaient comme avec le DLSS brut. Il s'active désormais dans toutes les configurations
  de résolution, scalées ou natives.
- **Zooms de caméra** (visée à l'arc, longue-vue) : la protection anti-transitions coupait
  le traitement du ciel pendant chaque zoom, laissant les nuages ghoster brièvement en
  plein jour. Elle ne se déclenche plus que sur les vraies coupures de scène (téléportations,
  transitions de sanctuaire), jamais sur les zooms.

**Comportement connu :** pendant environ la première minute après le chargement d'une
partie, un peu de ghosting de nuages peut apparaître, s'estomper et revenir pendant que le
système s'arme (il doit trouver la caméra du jeu, son tampon de profondeur, et bâtir son
historique de mouvement). Ensuite ça se stabilise pour le reste de la session. Cette
version est une **amélioration, pas la perfection** — un bref ghosting de nuages reste
possible dans certains moments précis.

### Amélioré : lignes noires / écho de cadre sombre pendant les mouvements de caméra

Le fameux « cadre noir fin » que certains joueurs voyaient en tournant la caméra avait un
vrai mécanisme derrière : le jeu laisse une bordure noire de 2 pixels sur son image aux
résolutions scalées, et l'historique DLSS étale cette bordure vers l'intérieur pendant le
mouvement — un écho pointillé proportionnel à la vitesse du pan. La bordure est désormais
réparée avant que DLSS ne voie l'image. Effet de bord bienvenu : les traînées de nuages
nourries par ce même historique pollué sont réduites elles aussi.

### Corrigé : plantage au passage plein écran (F11) ou au redimensionnement avec DLSS actif

Basculer en plein écran ou redimensionner la fenêtre pendant que DLSS tournait pouvait
faire planter l'émulateur (erreurs GPU sur le chemin de recréation du swapchain). La
séquence de recréation a été rebâtie — l'ancienne chaîne et ses vues ne partent que quand
l'affichage en a fini avec elles, et la boucle d'acquisition suit les règles Vulkan à la
lettre. Validé avec des centaines de bascules plein écran consécutives.

### Nouveau : auto-récupération de la génération d'images (Frame Generation)

La génération d'images pouvait s'arrêter en silence (affichage revenu à 1x, aucune erreur
nulle part) jusqu'à ce qu'un passage plein écran la ressuscite par hasard. Un chien de
garde détecte maintenant cet état et applique le même remède automatiquement en ~8
secondes. Pour le désactiver au besoin : variable d'environnement
`RYUJINX_DLSS_FG_WATCHDOG=0`.

### Corrigé : liste de jeux vide après un plantage

Si l'émulateur était tué ou plantait au moment exact où il sauvegardait sa configuration,
le fichier de config pouvait être tronqué — et votre liste de dossiers de jeux revenait
vide au lancement suivant. Les fichiers de configuration sont désormais écrits de façon
atomique : un plantage ne peut plus les abîmer.

### Corrigé : changer les réglages DLSS pouvait redémarrer avec un état partiel

Changer certains réglages DLSS redémarre l'émulateur ; le redémarrage pouvait revenir avec
seulement une partie du profil de qualité de mouvement appliquée (ghosting de nuages de
retour, correctif de bordure perdu). Le profil complet est maintenant reconstruit de zéro
à chaque redémarrage.

### Corrigé : corruption de textures avec « Forcer les mipmaps » + mods 4K en résolution native

Avec l'option *Forcer les mipmaps* (désactivée par défaut) **et** un mod de rendu 4K
in-engine en résolution émulateur native, des textures procédurales (portails de
sanctuaire, certains terrains) pouvaient montrer une corruption mauve/arc-en-ciel, surtout
autour des transitions de sanctuaire. Cause : des niveaux de mip étaient générés derrière
de petites cibles de rendu que le jeu redessine chaque frame. Ces textures sont désormais
exclues automatiquement dès que le jeu dessine dedans. Les textures d'art gardent tout le
bénéfice de l'option.

### Meilleurs diagnostics

Quand le système de qualité de mouvement DLSS ne peut pas tourner, il dit maintenant
pourquoi dans le journal (« caméra non appariée », « profondeur non appariée »...) au lieu
de rester muet. Si vous rapportez un problème, votre journal nous en dira beaucoup plus
qu'avant.

### Rappel — matériel requis pour le DLSS

Beaucoup de rapports se résument à ceci, alors soyons explicites :

- **Le DLSS et le DLAA exigent une carte NVIDIA RTX** (série RTX 20 ou plus récente). Ils
  **ne peuvent pas** fonctionner sur les cartes GTX (séries 10/16) — le matériel n'a tout
  simplement pas les unités sur lesquelles le DLSS tourne. C'est une limite NVIDIA, pas
  quelque chose que le fork peut contourner.
- **La génération d'images (Frame Generation) exige une RTX série 40 ou plus récente.**
- **Pas de carte RTX ? Utilisez NIS** (NVIDIA Image Scaling, inclus dans ce fork) : il
  fonctionne sur n'importe quel GPU et reste l'upscaler recommandé pour les cartes GTX et
  non-NVIDIA.
- Un petit nombre de configurations RTX plantent encore à l'activation du DLSS ; c'est en
  cours d'investigation active — voyez le ticket épinglé de build diagnostic si vous êtes
  concerné.

### Portée des tests

Cette version a été bâtie et validée sur une seule machine — Zelda TOTK et BOTW comme
jeux de test principaux, en configurations scalée et 4K natif avec mod, plus un test au
niveau du paquet sur les archives exactes publiées ici. D'autres GPU, pilotes et jeux peuvent se comporter différemment : si quelque chose
régresse chez vous, ouvrez un ticket avec votre journal et ça devient le correctif de
demain.
