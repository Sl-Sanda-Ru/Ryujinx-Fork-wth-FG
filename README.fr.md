# 🐺 The Beast Roofer Edition

Édition personnalisée de l'émulateur Nintendo Switch **Ryujinx**, pensée pour les **gros packs de textures HD/4K**, une image de qualité et une expérience de jeu fluide.

**🇬🇧 [English version available here](README.md)**

> ⚠️ **Émulateur uniquement.** Aucun jeu, aucune clé, aucun firmware.

---

## ⬇️ Télécharger

Dernière version :

👉 https://thebeastroofer-edition.com/rooferdev/the-beast-roofer-edition/releases

L'application se met ensuite à jour toute seule depuis le serveur du projet.

---

## 🐤 Build canary de diagnostic (temporaire)

Si l'émulateur plante chez vous quand DLSS est activé (au lancement d'un jeu,
ou en basculant plein écran / en changeant de résolution), une build spéciale
de diagnostic est disponible. C'est la v1.2.2 avec des journaux enrichis qui
nous aident à trouver la cause — aucune nouveauté, et elle ne se met jamais à
jour toute seule.

👉 Téléchargement et mode d'emploi : https://thebeastroofer-edition.com/rooferdev/the-beast-roofer-edition/issues/2

Rappel : DLSS exige une carte NVIDIA RTX. Les cartes GTX, les graphiques
intégrés, AMD et Intel ne peuvent pas du tout faire tourner DLSS.

---

## 💬 Communauté

Discussions, configs, questions : rejoins la communauté sur Reddit.

👉 https://www.reddit.com/r/BeastRooferEdition

---

## ✨ Ce que cette édition ajoute

### 🟢 NVIDIA DLSS, DLAA et Frame Generation (nouveau, RTX uniquement, optionnel)

Un pipeline DLSS complet, avec de vrais réglages dans **Paramètres, Graphismes** :

DLSS avec quatre modes : DLAA (qualité maximale), Quality, Balanced et Performance.
Frame Generation (RTX série 40 et 50) : désactivée, 2x ou 3x.
Le preset du modèle DLSS à ton choix : K, M, L, J ou F.
Un changement s'applique au redémarrage de l'application : un clic sur Oui et elle se relance toute seule.

Le paquet fournit le dossier `dlss` avec les six fichiers Streamline de NVIDIA (licence MIT). Les DLL du moteur DLSS ne sont pas incluses (raisons légales) : dans ce même dossier `dlss`, ajoute, pris d'un jeu DLSS que tu possèdes, `nvngx_dlss.dll` pour l'upscaling, `nvngx_dlssg.dll` pour la Frame Generation, et `NvLowLatencyVk.dll` si ton pilote ne la fournit pas déjà.

Je ne vous vends pas un rêve : tout ceci est encore expérimental. Ça fonctionne sur ma machine et sur les jeux que j'ai testés, mais chaque jeu est différent et il reste du travail. Si quelque chose cloche, dites le moi.

### 🧬 Génération de mipmaps à la volée (nouveau)

Beaucoup de jeux Switch streament leurs textures sans mipmaps, ce qui fourmille dès qu'on upscale. Une case à cocher dans les réglages graphiques et l'émulateur fabrique les chaînes manquantes en mémoire, automatiquement, pour n'importe quel jeu. Aucun pack, aucun fichier, rien de par jeu.

### 🎬 TAA natif (anti-crénelage temporel)

Option dans **Paramètres, Graphismes, Anti-crénelage**. Rendu doux, cinématographique, peu de scintillement. Fonctionne sur toutes les cartes graphiques, aucun fichier à ajouter. Meilleur en haute résolution interne.

### 🧩 Correctif packs de textures HD/4K

Corrige un plantage qui empêchait de charger les gros packs de textures ASTC (par exemple les packs 4K de Zelda BOTW et TOTK). Les grandes textures sont décodées tranche par tranche au lieu de déborder la mémoire, bien plus stable avec les packs haute résolution.

### 🖥️ Mode VRR (VSync variable)

Laisse un écran G-Sync ou FreeSync suivre la cadence du jeu, avec un plafond FPS réglable par jeu (Off, 30, 60, 120). Image plus douce sur les écrans compatibles.

### 🔄 Mises à jour automatiques

L'application se met à jour toute seule depuis le serveur officiel du projet.

---

## 🖥️ Prérequis

8 Go de RAM, un processeur 6 cœurs recommandé, une carte graphique des 10 dernières années.
OpenGL 4.6 ou Vulkan 1.4.
Windows 10 (20H1 ou plus récent), macOS Big Sur (Apple Silicon).
Pour le DLSS : une carte NVIDIA RTX. Pour la Frame Generation : une RTX série 40 ou 50.

---

## 🙏 Crédits et licence

Basé sur **Ryujinx**, l'émulateur open source créé par **gdkchan** et maintenu par l'équipe **Ryubing**.

Projet d'origine : https://git.ryujinx.app
Licence **MIT**, voir `LICENSE.txt`.

Code original de ce fork par **The Roofer Dev** (© 2026), sous licence MIT : intégration clean room de NVIDIA DLSS, DLAA et NIS, TAA natif, HDR scRGB natif, cap VRR par jeu, génération de mipmaps à la volée. Code et intégration uniquement : les technologies DLSS, DLAA et NIS appartiennent à NVIDIA. NVIDIA, DLSS, DLAA et NIS sont des marques de NVIDIA Corporation. Projet indépendant, ni affilié à, ni approuvé par NVIDIA.

---

## ⚠️ Avertissement

**Aucune affiliation avec Nintendo.** Ce projet n'est en aucune façon associé, approuvé ou parrainé par Nintendo. « Nintendo Switch » et les noms de jeux sont des marques de leurs propriétaires respectifs.

---

## 🚫 Zéro contenu propriétaire

Ici, il n'y a **aucune clé et aucun firmware. Zéro. Nada.** Cet émulateur ne contient, et ne contiendra jamais, aucun jeu, aucune clé, aucun firmware. Tu dois fournir les tiens, légalement extraits d'une console que tu possèdes.

**Nous n'encourageons en aucune façon le piratage**, et nous ne partagerons jamais de jeux, de clés ou de firmwares. Ce projet sert uniquement à faire tourner du contenu que tu possèdes légalement.

---

## 🆓 Bénévole et gratuit

Ceci est un projet **gratuit, bénévole et non commercial**. Il ne rapporte aucun argent, n'accepte aucun don et ne vend rien. Du temps personnel offert pour la communauté.

Des questions ou un commentaire ? **therooferdev@gmail.com**
