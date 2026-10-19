# Provenance

The implementation and tests in this repository were extracted from the
MIT-licensed CopperStart source tree. No original Smart File System C files,
ROM images, disk images or game media are included.

The on-disk layout work used the original Smart File System structures as
references, especially `blockstructure.h`, `objects.h`, `nodes.h`,
`btreenodes.h`, `bitmap.h`, `adminspaces.h`, `transactions.h` and
`support.c` in the
[AROS SFS tree](https://github.com/aros-development-team/AROS/tree/master/rom/filesys/SFS/FS).
The [original Smart File System source project](https://sourceforge.net/projects/smartfilesystem/)
publishes its C implementation under LGPLv2. Its name and license do not
describe this repository's ownership or license.

Compatibility checks also used independently formatted SFS images and
[amidisk](https://github.com/alfishe/amidisk) for structural checks.
Contributors should state the origin and license of any imported source or
fixture before adding it.
