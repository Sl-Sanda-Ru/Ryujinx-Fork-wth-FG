# The Beast Roofer Edition, mise à jour DLSS

**Le DLSS de NVIDIA arrive dans The Beast Roofer Edition.**

Cette mise à jour apporte un pipeline DLSS complet au moteur Vulkan, avec de vrais réglages dans l'interface. Tout est optionnel, le comportement par défaut reste 100 % d'origine.

## Ce que tu peux activer dans Réglages, Graphismes

DLSS avec quatre modes : DLAA (qualité maximale), Quality, Balanced et Performance. Le jeu rend à une résolution interne réduite et DLSS reconstruit l'image, gros gains de FPS à qualité quasi native.

Frame Generation (RTX série 40 et 50) : désactivée, 2x ou 3x. Des images supplémentaires générées pour un affichage deux ou trois fois plus fluide.

Le preset du modèle DLSS, à ton choix : K, M, L, J ou F.

Génération de mipmaps à la volée : beaucoup de jeux Switch streament leurs textures sans mipmaps, ce qui fourmille dès qu'on upscale. L'émulateur fabrique les chaînes manquantes en mémoire, automatiquement, pour n'importe quel jeu. Aucun pack, aucun fichier, rien de par jeu.

Les vrais motion vectors : l'émulateur calcule des vecteurs de mouvement fidèles à la caméra et les fournit à DLSS et à la Frame Generation, avec un traitement dédié du ciel et des nuages.

Un changement de réglage DLSS s'applique au redémarrage de l'application : un clic sur Oui dans la fenêtre qui apparaît et elle se relance toute seule.

## Un mot honnête

Je ne vous vends pas un rêve. Tout ceci est encore expérimental. Ça fonctionne sur ma machine et sur les jeux que j'ai testés, mais chaque jeu est différent et il reste du travail. Si quelque chose cloche chez vous, dites le moi, c'est comme ça qu'on avance.

## Prérequis et notes

GPU NVIDIA RTX. La Frame Generation demande une RTX série 40 ou 50.

Le paquet fournit le dossier `dlss`, à côté de l'exécutable, avec les six fichiers Streamline de NVIDIA (licence MIT) : sl.interposer.dll, sl.common.dll, sl.dlss.dll, sl.dlss_g.dll, sl.pcl.dll et sl.reflex.dll.

Les DLL du moteur DLSS ne sont pas incluses. Dans ce même dossier `dlss`, ajoutez, pris d'un jeu DLSS que vous possédez : nvngx_dlss.dll pour l'upscaling, nvngx_dlssg.dll pour la Frame Generation, et NvLowLatencyVk.dll si elle n'est pas déjà fournie par votre pilote.

Si un mod propose sa propre résolution ou son propre upscale (par exemple TOTK Optimizer), réglez le sur natif, 1x. Un seul upscaler dans la chaîne : l'émulateur et DLSS s'occupent de la résolution.

Avec la Frame Generation et le HDR actifs, l'encodage d'écran passe automatiquement en PQ : même lumière, encodage compatible avec la Frame Generation.

Journalisation graphique sur Aucun pour les pleines performances.

Compatible avec la sortie HDR des versions précédentes.

## Le jitter caméra

On a aussi réussi le jitter caméra, la technique qui décale l'image d'une fraction de pixel à chaque frame pour que DLSS reconstruise encore plus de détail. Il fonctionne, mais il fait scintiller certaines textures au loin, surtout les arbres. Je cherche encore le bon moyen de régler ça, alors il est livré désactivé pour l'instant. Si quelqu'un a une idée, venez m'en parler.

## Note

Si vous activez le DLSS, mettez l'anti-crénelage sur Aucun (le TAA et le DLSS ensemble peuvent faire planter le lancement d'un jeu, un garde-fou automatique arrivera dans une prochaine version).

Dans les jeux à résolution dynamique, la Frame Generation peut se désactiver en mode Quality ou Performance. Verrouillez la résolution du jeu si son mod le permet.
