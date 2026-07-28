from __future__ import annotations

import tinta_worker as worker
import tinta_quality_guard


_original_init = worker.TintaTranslator.__init__


def _responsive_init(
    self,
    params: dict,
    supplemental_regions: list[dict] | None = None,
    bright_candidates: list[dict] | None = None,
) -> None:
    adjusted = dict(params)
    # En Manga Image Translator, models_ttl=0 fuerza la precarga conjunta de
    # detector, OCR e inpainter. El worker ya es residente, así que conviene
    # cargarlos cuando cada fase los necesita y conservarlos durante diez minutos.
    if int(adjusted.get("models_ttl", 0) or 0) == 0:
        adjusted["models_ttl"] = 600
    _original_init(self, adjusted, supplemental_regions, bright_candidates)


worker.TintaTranslator.__init__ = _responsive_init
tinta_quality_guard.apply(worker)


if __name__ == "__main__":
    raise SystemExit(worker.main())
