from __future__ import annotations

import tinta_worker as worker
import tinta_quality_guard


_original_init = worker.TintaTranslator.__init__
_original_mask_refinement = worker.TintaTranslator._run_mask_refinement
_original_inpainting = worker.TintaTranslator._run_inpainting


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


async def _bounded_mask_refinement(self, config, ctx):
    """Impide que el refinador convierta letras en placas rectangulares.

    La máscara final puede mejorar el contorno detectado, pero nunca alejarse de los
    trazos reales de CTD/OCR. Así el inpainter no recibe un rectángulo que sobresalga
    del bocadillo.
    """

    refined = await _original_mask_refinement(self, config, ctx)
    if ctx.mask_raw is None:
        return refined

    raw = ctx.mask_raw
    if raw.ndim == 3:
        raw = worker.cv2.cvtColor(raw, worker.cv2.COLOR_BGR2GRAY)
    seed = worker.np.where(raw > 20, 255, 0).astype(worker.np.uint8)
    seed = worker.cv2.dilate(
        seed,
        worker.cv2.getStructuringElement(worker.cv2.MORPH_ELLIPSE, (9, 9)),
        iterations=1,
    )

    supplemental = worker.create_supplemental_mask(
        ctx.img_rgb,
        self.supplemental_regions,
        seed,
    )
    seed = worker.cv2.bitwise_or(seed, supplemental)
    if not worker.np.any(seed):
        return refined

    # El refinado solo puede crecer unos pocos píxeles alrededor de los glifos.
    # Un rectángulo generado por la librería queda fuera de este soporte y se elimina.
    support = worker.cv2.dilate(
        seed,
        worker.cv2.getStructuringElement(worker.cv2.MORPH_ELLIPSE, (19, 19)),
        iterations=1,
    )

    if refined is None:
        return seed
    if refined.ndim == 3:
        refined = worker.cv2.cvtColor(refined, worker.cv2.COLOR_BGR2GRAY)
    refined = worker.np.where(refined > 20, 255, 0).astype(worker.np.uint8)
    bounded = worker.cv2.bitwise_and(refined, support)
    return worker.cv2.bitwise_or(seed, bounded)


async def _masked_inpainting(self, config, ctx):
    """Conserva el original fuera de la máscara, aunque LaMa devuelva un parche entero."""

    inpainted = await _original_inpainting(self, config, ctx)
    if inpainted is None or ctx.mask is None or ctx.img_rgb is None:
        return inpainted

    original = worker.np.asarray(ctx.img_rgb)
    result = worker.np.asarray(inpainted)
    if result.shape != original.shape:
        return inpainted

    mask = ctx.mask
    if mask.ndim == 3:
        mask = worker.cv2.cvtColor(mask, worker.cv2.COLOR_BGR2GRAY)
    if mask.shape[:2] != original.shape[:2]:
        mask = worker.cv2.resize(
            mask,
            (original.shape[1], original.shape[0]),
            interpolation=worker.cv2.INTER_NEAREST,
        )

    active = mask > 20
    composited = original.copy()
    composited[active] = result[active]
    return composited


worker.TintaTranslator.__init__ = _responsive_init
worker.TintaTranslator._run_mask_refinement = _bounded_mask_refinement
worker.TintaTranslator._run_inpainting = _masked_inpainting
tinta_quality_guard.apply(worker)


if __name__ == "__main__":
    raise SystemExit(worker.main())
