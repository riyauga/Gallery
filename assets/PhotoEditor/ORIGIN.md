# Source

Copied from the installed Microsoft Windows Photos 2026.11090.22001.0,
ImageEditorLib/Assets/web/ImageEditor (49 files, 4,883,117 bytes).
Chinese strings were exported from ImageEditorLib.pri with Windows SDK makepri.

Integration changes: index.html loads Gallery host.js; main.js resolves hvcEvents
from window instead of the original synchronous COM host object. Editor algorithms,
rendering and animations are unchanged. Optional cloud/native AI services are disabled.

REA static Evidence: ev_c69a8308f15eb7161b1731a4b350330fc9396156983699abf82988bbf4c399e2.
Static relationship coverage is incomplete; runtime integration is validated separately.

Original third-party notices from PhotosManager/Assets/OSS_Notices/OSS_Notice.txt
are retained as static/js/main.js.LICENSE.txt, matching the bundle's notice reference.
AI integration was deferred by the project owner; no model/native AI service is shipped.
