# Release notes v1.2.4 — The Beast Roofer Edition (EN + FR)

_Pour la page de release Gitea. Bilingue (règle du fork). Sans jargon interne._

---

## English

I am shipping this update because I caught a bug I had introduced myself in v1.2.3, the
one that turns the screen green in some games. It could in principle affect any game, so
please take this update even if yours looked fine.

One correction while I am at it: the black block artifacts some of you saw in
motion-blur scenes, the ones I had described as a long-standing emulation defect and worked around by skipping the
motion-blur pass, were caused by that same bug of mine. They were never an old emulator
problem, they were mine. They are gone now, and the motion blur stays on.

### Fixed: green screen in several games

Some games — several Unreal Engine titles among them — rendered their menus and videos
correctly but showed a green (or corrupted) screen the moment 3D gameplay started. The
cause was a real bug in this fork, introduced in v1.2.3: a constant used by the DLSS
system was never delivered to the GPU when DLSS jitter was off, and every 3D shader ended
up reading uninitialized memory. Depending on what the graphics driver had left in that
memory, a game could look perfectly fine, glitch occasionally, or go fully green.

The offending code has been removed: with jitter off — which is how this fork ships — the
vertex shaders are now translated exactly as the base emulator does, so there is nothing
left to corrupt. To be clear about what this is: we broke it, and we undid it.

The same bug also produced black block artifacts in motion-blur scenes on this fork (that
pass expands points into quads; hand it undefined vertex positions and it draws blocks).
Those are gone too, and the motion blur no longer has to be disabled to avoid them.
Verified on the previously-broken games. If a specific title still shows issues, it is a
separate cause — please report it on the community.

**Every v1.2.3 user should take this update**: the bug could in principle touch any
game, even ones that appeared to work.

### New: DLSS Sharpening slider

A **DLSS Sharpening** slider (0–100) in Settings → Graphics, right under Frame Generation.
It applies the same contrast-adaptive sharpening used by the FSR filter, directly on the
DLSS output — HDR is handled correctly and it costs almost nothing. 0 = off (the default;
existing setups are unchanged). Around 25–40 is subtle; higher is noticeably crisper but
can make halos and shimmer more visible in motion. Applied on the next launch, like the
other DLSS settings.

An experimental `Advanced motion stack` checkbox is also present, **off by default**. It
improves camera tracking: it reads the camera even when a game stores it in an unusual place,
it rejects misleading matrices such as sky or reflections, and it stops mistaking the game's
own sampling jitter for real motion. It removes nothing from the image. It has only been
tested on Xenoblade Chronicles 2, and it can add trembling in motion on other games, so leave
it off unless you want to experiment.

### Requirements

DLSS in all its modes — DLAA, Quality, Balanced and Performance — needs an NVIDIA RTX card,
RTX 20 series or newer. Frame Generation needs an RTX 40 or 50 series. If you do not have an
RTX card, NIS is included in this fork and runs on any GPU, and the green screen fix above
matters to you either way.

The DLSS engine files are not in the package, because they are NVIDIA's proprietary DLLs and I
will not redistribute them. What the package does ship is the `dlss` folder next to the
executable, with NVIDIA's six Streamline files, which are MIT licensed. In that same folder you
add your own, taken from a DLSS game you own: `nvngx_dlss.dll` for upscaling, `nvngx_dlssg.dll`
for Frame Generation, which the NVIDIA driver usually provides already, and `NvLowLatencyVk.dll`
if your driver does not.

### Known issue: Frame Generation still produces ghost images

With Frame Generation enabled, ghost or "temporal" images can still appear — doubled
edges and trails on moving content. **Turning Frame Generation off removes them.** This
is the number one thing being worked on right now: the generated frames are built from
motion vectors we reconstruct, and those describe the camera better than they describe
individual moving objects. If ghosting bothers you more than the extra smoothness helps,
set Frame Generation to Off in the graphics settings.

### Known issue: loading can hang on some games

On some games, loading (or the menu) occasionally stops progressing — the
picture keeps drawing, but the game stops advancing. Closing and reopening the emulator
clears it. We captured this live several times and it is **not solved yet**: the freeze
happens on the game's own side, and it predates the changes in this release. If you hit
it, closing and relaunching is the workaround for now.

---

## Français

J'envoie cette mise à jour parce que j'ai attrapé un bug que j'avais moi-même introduit en
v1.2.3, celui qui rend l'écran vert dans certains jeux. Il pouvait en principe toucher
n'importe quel jeu, alors prenez la mise à jour même si le vôtre semblait correct.

Une correction au passage : les blocs noirs que certains ont vus dans les scènes avec
flou de mouvement, ceux que j'avais décrits comme un vieux défaut d'émulation et que je contournais en désactivant la passe de
flou de mouvement, venaient du même bug — le mien. Ça n'a jamais été un problème ancien de
l'émulateur, c'était moi. Ils sont partis, et le flou de mouvement reste actif.

### Corrigé : écran vert dans plusieurs jeux

Certains jeux — plusieurs titres Unreal Engine notamment — affichaient correctement leurs
menus et vidéos, mais montraient un écran vert (ou corrompu) dès que la 3D commençait.
La cause était un vrai bug de ce fork, introduit en v1.2.3 : une constante du système
DLSS n'était jamais livrée au GPU quand le jitter DLSS était éteint, et tous les shaders
3D lisaient de la mémoire non initialisée. Selon ce que le pilote graphique avait laissé
dans cette mémoire, un jeu pouvait sembler parfait, glitcher par moments, ou devenir
entièrement vert.

Le code fautif a été retiré : avec le jitter éteint — c'est ainsi que ce fork est livré —
les vertex shaders sont désormais traduits exactement comme le fait l'émulateur de base,
il n'y a donc plus rien à corrompre. Pour être clair sur ce que c'est : on l'avait cassé,
on l'a défait.

Le même bug produisait aussi les blocs noirs dans les scènes avec flou de mouvement sur ce
fork (cette passe dilate des points en carrés ; donnez-lui des positions indéfinies, elle
dessine des blocs). Ils disparaissent également, et il n'est plus nécessaire de désactiver
le flou de mouvement pour les éviter. Vérifié sur les jeux qui étaient cassés. Si un titre
précis montre encore des problèmes, c'est une cause distincte — signalez-le sur la
communauté.

**Tous les utilisateurs de la v1.2.3 devraient prendre cette mise à jour** : le bug
pouvait en principe toucher n'importe quel jeu, même ceux qui semblaient fonctionner.

### Nouveau : curseur de netteté DLSS

Un curseur **DLSS Sharpening** (0–100) dans Paramètres → Graphismes, juste sous la Frame
Generation. Il applique la même netteté adaptative que le filtre FSR, directement sur la
sortie DLSS — le HDR est géré correctement et le coût est négligeable. 0 = désactivé (le
défaut ; les configurations existantes ne changent pas). Autour de 25–40 c'est subtil ;
plus haut, c'est nettement plus croustillant mais les halos et le scintillement en
mouvement deviennent plus visibles. Appliqué au prochain lancement, comme les autres
réglages DLSS.

Une case `Advanced motion stack` expérimentale est également présente, **désactivée par
défaut**. Elle améliore le suivi de la caméra : elle lit la caméra même quand un jeu la range
dans un endroit inhabituel, elle rejette les matrices trompeuses comme le ciel ou les reflets,
et elle ne confond plus le tremblement d'échantillonnage du jeu avec du mouvement réel. Elle ne
retire rien de l'image. Elle n'a été testée que sur Xenoblade Chronicles 2, et elle peut au
contraire ajouter du tremblement en mouvement sur d'autres jeux, alors laissez-la éteinte sauf
si vous voulez expérimenter.

### Prérequis

Le DLSS dans tous ses modes — DLAA, Quality, Balanced et Performance — exige une carte NVIDIA
RTX, série 20 ou plus récente. La Frame Generation exige une RTX série 40 ou 50. Si vous n'avez
pas de carte RTX, le NIS est inclus dans ce fork et fonctionne sur n'importe quel GPU, et de
toute façon le correctif de l'écran vert ci-dessus vous concerne.

Les fichiers du moteur DLSS ne sont pas dans le paquet, parce que ce sont des DLL propriétaires
de NVIDIA et que je ne les redistribue pas. Ce que le paquet contient, c'est le dossier `dlss`
à côté de l'exécutable, avec les six fichiers Streamline de NVIDIA, sous licence MIT. Dans ce
même dossier vous ajoutez les vôtres, pris dans un jeu DLSS que vous possédez :
`nvngx_dlss.dll` pour la mise à l'échelle, `nvngx_dlssg.dll` pour la Frame Generation, que le
pilote NVIDIA fournit généralement déjà, et `NvLowLatencyVk.dll` si votre pilote ne le fournit
pas.

### Limite connue : la Frame Generation produit encore des images fantômes

Avec la Frame Generation activée, des images fantômes ou « temporelles » peuvent encore
apparaître — contours dédoublés et traînées sur ce qui bouge. **Désactiver la Frame
Generation les fait disparaître.** C'est le chantier numéro un en ce moment : les images
générées sont construites à partir de vecteurs de mouvement que nous reconstruisons, et
ceux-ci décrivent mieux la caméra que les objets qui bougent individuellement. Si les
fantômes vous gênent plus que la fluidité supplémentaire ne vous apporte, mettez la Frame
Generation sur Off dans les paramètres graphiques.

### Limite connue : le chargement peut se bloquer sur certains jeux

Sur certains jeux, le chargement (ou le menu) cesse parfois d'avancer — l'image
continue de s'afficher, mais le jeu n'avance plus. Fermer et rouvrir l'émulateur règle le
problème. Nous l'avons capturé en direct à plusieurs reprises et il **n'est pas encore
résolu** : le blocage se produit du côté du jeu lui-même, et il est antérieur aux
changements de cette version. Si ça vous arrive, fermer et relancer reste le contournement
pour l'instant.
