(function () {
    if (window.__tenantPublicImageUploader) return;
    window.__tenantPublicImageUploader = true;

    const root = document.documentElement;
    root.classList.add('tppa-js');

    // El navegador ya aplica la orientacion EXIF al mostrar y al dibujar en canvas, y el
    // resultado exportado se envia sin metadata. El backend igual normaliza y valida.
    const UNREADABLE_MESSAGE = 'No pudimos leer esta imagen. Proba con una foto en JPG, PNG o WEBP.';
    const HEIC_MESSAGE = 'Las fotos HEIC/HEIF de iPhone no se pueden procesar. Entra a Ajustes > Camara > ' +
        'Formatos y elegi "Mas compatible", o comparti la foto para convertirla a JPG.';

    const uploadForms = Array.from(document.querySelectorAll('[data-public-image-upload]'));
    if (uploadForms.length === 0) return;

    const statusBox = document.querySelector('[data-public-image-status]');
    let active = null;
    let objectUrl = null;
    let modal = null;

    uploadForms.forEach(form => {
        const input = form.querySelector('input[type="file"][name="file"]');
        if (!input) return;

        input.addEventListener('change', () => {
            const file = input.files && input.files[0];
            if (!file) return;

            if (isHeicLike(file)) {
                input.value = '';
                showStatus(HEIC_MESSAGE, true);
                return;
            }

            openCropper(form, input, file);
        });
    });

    document.querySelectorAll('[data-remove-form]').forEach(form => {
        form.addEventListener('submit', event => {
            event.preventDefault();
            submitRemove(form);
        });
    });

    function ensureModal() {
        if (modal) return modal;

        const wrapper = document.createElement('div');
        wrapper.className = 'tppa-crop-modal';
        wrapper.hidden = true;
        wrapper.innerHTML = [
            '<div class="tppa-crop-dialog" role="dialog" aria-modal="true" aria-label="Encuadrar imagen">',
            '  <div class="tppa-crop-head">',
            '    <div>',
            '      <h2>Encuadrar imagen</h2>',
            '      <p>Move la imagen y ajusta el zoom antes de subirla.</p>',
            '    </div>',
            '    <button type="button" class="tppa-crop-close" data-crop-cancel aria-label="Cerrar">x</button>',
            '  </div>',
            '  <div class="tppa-crop-presets" data-crop-presets role="group" aria-label="Formato de imagen"></div>',
            '  <p class="tppa-crop-help" data-crop-help></p>',
            '  <div class="tppa-crop-stage">',
            '    <div class="tppa-crop-frame" data-crop-frame>',
            '      <img alt="" data-crop-image draggable="false" />',
            '    </div>',
            '  </div>',
            '  <label class="tppa-crop-zoom" data-crop-zoom-wrap>Zoom',
            '    <input type="range" min="1" max="4" step="0.01" value="1" data-crop-zoom />',
            '  </label>',
            '  <div class="tppa-crop-error" data-crop-error hidden></div>',
            '  <div class="tppa-crop-actions">',
            '    <button type="button" class="tppa-btn tppa-btn-soft" data-crop-cancel>Cancelar</button>',
            '    <button type="button" class="tppa-btn tppa-btn-primary" data-crop-submit>Subir imagen</button>',
            '  </div>',
            '</div>'
        ].join('');

        document.body.appendChild(wrapper);

        modal = {
            wrapper,
            frame: wrapper.querySelector('[data-crop-frame]'),
            image: wrapper.querySelector('[data-crop-image]'),
            zoom: wrapper.querySelector('[data-crop-zoom]'),
            zoomWrap: wrapper.querySelector('[data-crop-zoom-wrap]'),
            presets: wrapper.querySelector('[data-crop-presets]'),
            help: wrapper.querySelector('[data-crop-help]'),
            submit: wrapper.querySelector('[data-crop-submit]'),
            error: wrapper.querySelector('[data-crop-error]'),
            cancelButtons: Array.from(wrapper.querySelectorAll('[data-crop-cancel]'))
        };

        modal.cancelButtons.forEach(button => button.addEventListener('click', closeCropper));
        modal.zoom.addEventListener('input', () => {
            if (!active) return;
            active.zoom = parseFloat(modal.zoom.value) || 1;
            renderImage();
        });
        modal.submit.addEventListener('click', submitCrop);

        modal.frame.addEventListener('pointerdown', event => {
            if (!active) return;
            modal.frame.setPointerCapture(event.pointerId);
            active.drag = {
                x: event.clientX,
                y: event.clientY,
                offsetX: active.offsetX,
                offsetY: active.offsetY
            };
        });
        modal.frame.addEventListener('pointermove', event => {
            if (!active || !active.drag) return;
            active.offsetX = active.drag.offsetX + (event.clientX - active.drag.x);
            active.offsetY = active.drag.offsetY + (event.clientY - active.drag.y);
            renderImage();
        });
        modal.frame.addEventListener('pointerup', () => {
            if (active) active.drag = null;
        });
        modal.frame.addEventListener('pointercancel', () => {
            if (active) active.drag = null;
        });

        document.addEventListener('keydown', event => {
            if (event.key === 'Escape' && modal && !modal.wrapper.hidden) {
                closeCropper();
            }
        });

        return modal;
    }

    function openCropper(form, input, file) {
        const ui = ensureModal();
        revokeObjectUrl();
        objectUrl = URL.createObjectURL(file);

        const fallbackAspect = parseFloat(form.dataset.cropAspect || '1') || 1;
        const presets = parsePresets(form.dataset.cropPresets, form.dataset.cropLabels, fallbackAspect);
        const output = {
            maxWidth: parseInt(form.dataset.outputMaxWidth || '0', 10) || 0,
            maxHeight: parseInt(form.dataset.outputMaxHeight || '0', 10) || 0,
            type: form.dataset.outputType === 'image/png' ? 'image/png' : 'image/jpeg'
        };

        ui.error.hidden = true;
        ui.submit.disabled = false;
        ui.submit.textContent = 'Subir imagen';

        active = {
            form,
            input,
            file,
            output,
            presets,
            preset: presets[0],
            aspect: presets[0].aspect || fallbackAspect,
            zoom: 1,
            offsetX: 0,
            offsetY: 0,
            baseScale: 1,
            containScale: 1,
            coverScale: 1,
            drag: null,
            naturalWidth: 0,
            naturalHeight: 0
        };

        buildPresetButtons(ui, presets);

        ui.image.onload = () => {
            if (!active) return;
            active.naturalWidth = ui.image.naturalWidth;
            active.naturalHeight = ui.image.naturalHeight;
            applyPreset(pickDefaultPreset(form, presets, active.naturalWidth, active.naturalHeight));
        };
        // El navegador no pudo decodificar el archivo (tipico con HEIC fuera de iPhone).
        ui.image.onerror = () => {
            closeCropper();
            showStatus(isHeicLike(file) ? HEIC_MESSAGE : UNREADABLE_MESSAGE, true);
        };
        ui.image.src = objectUrl;

        ui.wrapper.hidden = false;
        document.body.classList.add('tppa-crop-open');
    }

    // Tokens: "original" | "W:H" (Cover) | "padded:W:H" | "contain:W:H" | "cover:W:H".
    // Vienen del perfil del servidor (PublicImageProfile), junto con sus etiquetas.
    function parsePresets(raw, labelsRaw, fallbackAspect) {
        const tokens = (raw || '4:5,original').split(',').map(t => t.trim()).filter(Boolean);
        const labels = (labelsRaw || '').split('|');
        const list = [];
        const seen = {};
        tokens.forEach((token, index) => {
            const preset = parsePresetToken(token, fallbackAspect);
            if (preset && !seen[preset.key]) {
                if (labels[index]) preset.label = labels[index];
                seen[preset.key] = true;
                list.push(preset);
            }
        });
        if (list.length === 0) {
            list.push({ key: 'cover:' + fallbackAspect, label: 'Recorte', aspect: fallbackAspect, fitMode: 'Cover' });
        }
        return list;
    }

    function parsePresetToken(token, fallbackAspect) {
        const lower = token.toLowerCase();
        if (lower === 'original') {
            return { key: 'original', label: 'Original', aspect: null, fitMode: 'Original' };
        }

        let fitMode = 'Cover';
        let aspectPart = lower;
        if (lower.indexOf('padded:') === 0) { fitMode = 'Padded'; aspectPart = lower.slice(7); }
        else if (lower.indexOf('contain:') === 0) { fitMode = 'Contain'; aspectPart = lower.slice(8); }
        else if (lower.indexOf('cover:') === 0) { fitMode = 'Cover'; aspectPart = lower.slice(6); }

        const aspect = parseAspect(aspectPart) || fallbackAspect;
        return {
            key: fitMode.toLowerCase() + ':' + aspectPart,
            label: presetLabel(fitMode, aspectPart),
            aspect: aspect,
            fitMode: fitMode
        };
    }

    function parseAspect(text) {
        const parts = String(text).split(':');
        if (parts.length === 2) {
            const w = parseFloat(parts[0]);
            const h = parseFloat(parts[1]);
            if (w > 0 && h > 0) return w / h;
        }
        const single = parseFloat(text);
        return single > 0 ? single : 0;
    }

    function presetLabel(fitMode, aspectPart) {
        if (fitMode === 'Padded') return 'Completa con fondo';
        if (fitMode === 'Contain') return 'Contener';
        const names = {
            '4:5': 'Vertical 4:5',
            '3:4': 'Vertical 3:4',
            '1:1': 'Cuadrado 1:1',
            '4:3': 'Horizontal 4:3',
            '16:9': 'Portada 16:9',
            '2:1': 'Portada amplia 2:1'
        };
        return names[aspectPart] || ('Formato ' + aspectPart);
    }

    function pickDefaultPreset(form, presets, naturalWidth, naturalHeight) {
        const isVertical = naturalHeight > naturalWidth;
        const wanted = (isVertical && form.dataset.cropDefaultVertical)
            ? form.dataset.cropDefaultVertical
            : (form.dataset.cropDefault || '');
        const match = presets.filter(p => p.key === wanted.toLowerCase() ||
            p.key.indexOf(wanted.toLowerCase()) === 0 ||
            p.label.toLowerCase() === wanted.toLowerCase());
        return match[0] || presets[0];
    }

    function buildPresetButtons(ui, presets) {
        ui.presets.innerHTML = '';
        presets.forEach(preset => {
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'tppa-crop-preset';
            button.dataset.presetKey = preset.key;
            button.textContent = preset.label;
            button.addEventListener('click', () => applyPreset(preset));
            ui.presets.appendChild(button);
        });
    }

    function applyPreset(preset) {
        if (!active || !modal || !preset) return;
        active.preset = preset;
        active.aspect = preset.aspect || (active.naturalWidth / active.naturalHeight) || 1;

        // Boton activo.
        Array.from(modal.presets.children).forEach(btn => {
            btn.classList.toggle('is-active', btn.dataset.presetKey === preset.key);
        });

        // El zoom (recorte manual) solo aplica en modo Cover.
        const isCover = preset.fitMode === 'Cover';
        modal.zoomWrap.style.display = isCover ? '' : 'none';

        // En Original el marco toma el aspecto real de la foto (se ve completa sin letterbox).
        const frameAspect = preset.fitMode === 'Original'
            ? (active.naturalWidth / active.naturalHeight) || 1
            : active.aspect;
        modal.frame.style.setProperty('--tppa-crop-ratio', String(frameAspect));
        modal.help.textContent = presetHelp(preset.fitMode);

        modal.zoom.value = '1';
        active.zoom = 1;
        centerImage();
    }

    // Sin tecnicismos: nada de proporciones, pixeles ni formatos para la duena del negocio.
    function presetHelp(fitMode) {
        if (fitMode === 'Padded' || fitMode === 'Contain') {
            return 'Muestra la fotografia completa sin recortarla.';
        }
        if (fitMode === 'Original') {
            return 'Se sube con la proporcion original de la foto.';
        }
        return 'Mueve la foto y usa el zoom si quieres ajustar el encuadre.';
    }

    function centerImage() {
        if (!active || !modal) return;
        const frame = modal.frame.getBoundingClientRect();
        active.coverScale = Math.max(
            frame.width / active.naturalWidth,
            frame.height / active.naturalHeight);
        active.containScale = Math.min(
            frame.width / active.naturalWidth,
            frame.height / active.naturalHeight);

        // Cover: zoom 1 = el minimo necesario para LLENAR el marco, nunca mas. Si la foto ya
        // tiene la proporcion del marco (una vertical 3:4 de celular), ese minimo la muestra
        // entera: el recorte lo decide despues la persona con el zoom.
        // Otros modos: la foto entra completa (contain).
        active.baseScale = (active.preset && active.preset.fitMode === 'Cover')
            ? active.coverScale
            : active.containScale;

        active.offsetX = 0;
        active.offsetY = 0;
        renderImage();
    }

    function renderImage() {
        if (!active || !modal) return;

        const frame = modal.frame.getBoundingClientRect();
        const scale = active.baseScale * active.zoom;
        const displayWidth = active.naturalWidth * scale;
        const displayHeight = active.naturalHeight * scale;
        const maxOffsetX = Math.max(0, (displayWidth - frame.width) / 2);
        const maxOffsetY = Math.max(0, (displayHeight - frame.height) / 2);

        active.offsetX = clamp(active.offsetX, -maxOffsetX, maxOffsetX);
        active.offsetY = clamp(active.offsetY, -maxOffsetY, maxOffsetY);

        const left = (frame.width - displayWidth) / 2 + active.offsetX;
        const top = (frame.height - displayHeight) / 2 + active.offsetY;

        modal.image.style.width = `${displayWidth}px`;
        modal.image.style.height = `${displayHeight}px`;
        modal.image.style.transform = `translate(${left}px, ${top}px)`;
    }

    function calculateCrop() {
        const frame = modal.frame.getBoundingClientRect();
        const scale = active.baseScale * active.zoom;
        const displayWidth = active.naturalWidth * scale;
        const displayHeight = active.naturalHeight * scale;
        const left = (frame.width - displayWidth) / 2 + active.offsetX;
        const top = (frame.height - displayHeight) / 2 + active.offsetY;

        const cropX = clamp(Math.round((0 - left) / scale), 0, active.naturalWidth - 1);
        const cropY = clamp(Math.round((0 - top) / scale), 0, active.naturalHeight - 1);
        const cropWidth = clamp(Math.round(frame.width / scale), 1, active.naturalWidth - cropX);
        const cropHeight = clamp(Math.round(frame.height / scale), 1, active.naturalHeight - cropY);

        return { cropX, cropY, cropWidth, cropHeight };
    }

    async function submitCrop() {
        if (!active || !modal) return;

        const preset = active.preset || { fitMode: 'Cover' };
        const formData = new FormData(active.form);

        // Camino preferido: el navegador exporta la imagen ya encuadrada y al tamano que
        // necesita la landing, asi una foto de 48 MP no viaja entera por la red. Si el canvas
        // falla se envia el archivo original y el backend hace todo el trabajo.
        const exported = await tryExportFramedImage(preset);

        if (exported) {
            // El recorte ya viene aplicado en los pixeles exportados: se envia el mismo modo y
            // aspecto (sin CropX/Y/W/H), asi el backend valida el formato y no vuelve a recortar.
            formData.set('file', exported.file);
            formData.set('FitMode', preset.fitMode);
            if (preset.fitMode !== 'Original' && preset.aspect) {
                formData.set('TargetAspectRatio', String(preset.aspect));
            }
        } else {
            formData.set('file', active.file);
            formData.set('FitMode', preset.fitMode);

            if (preset.fitMode !== 'Original' && preset.aspect) {
                formData.set('TargetAspectRatio', String(preset.aspect));
            }

            // El recorte manual (CropX/Y/W/H) solo aplica en modo Cover; en los demas el
            // backend contiene/rellena la imagen completa sin recortar.
            if (preset.fitMode === 'Cover') {
                const crop = calculateCrop();
                formData.set('CropX', String(crop.cropX));
                formData.set('CropY', String(crop.cropY));
                formData.set('CropWidth', String(crop.cropWidth));
                formData.set('CropHeight', String(crop.cropHeight));
            }
        }

        modal.submit.disabled = true;
        modal.submit.textContent = 'Subiendo...';
        modal.error.hidden = true;

        try {
            const response = await fetch(active.form.action, {
                method: 'POST',
                body: formData,
                headers: {
                    'X-Requested-With': 'XMLHttpRequest',
                    'Accept': 'application/json'
                }
            });

            const data = await response.json();
            if (!response.ok || !data.success) {
                throw new Error(data.message || 'No fue posible subir la imagen.');
            }

            applyUploadResult(active.form, data);
            showStatus(data.message || 'Imagen actualizada.', false);
            closeCropper();
        } catch (error) {
            modal.error.textContent = error.message || 'No fue posible subir la imagen.';
            modal.error.hidden = false;
            modal.submit.disabled = false;
            modal.submit.textContent = 'Subir imagen';
        }
    }

    /**
     * Dibuja la imagen ya encuadrada en un canvas del tamano que realmente usa la landing y
     * devuelve un File listo para subir. Devuelve null si el navegador no puede hacerlo.
     * Cover: se aplica el recorte visible. Otros modos: solo se reduce (el backend compone).
     */
    async function tryExportFramedImage(preset) {
        try {
            if (!modal || !active || !active.naturalWidth || !active.naturalHeight) return null;
            if (typeof document.createElement('canvas').toBlob !== 'function') return null;

            const maxWidth = active.output.maxWidth || active.naturalWidth;
            const maxHeight = active.output.maxHeight || active.naturalHeight;
            const canvas = document.createElement('canvas');
            const context = canvas.getContext('2d');
            if (!context) return null;

            if (preset.fitMode === 'Cover') {
                const crop = calculateCrop();
                const aspect = preset.aspect || (crop.cropWidth / crop.cropHeight) || 1;
                const frame = fitInside(aspect, maxWidth, maxHeight);
                // Nunca se amplia: si el recorte es mas chico que el marco, manda el recorte.
                const width = Math.max(1, Math.round(Math.min(frame.width, crop.cropWidth)));
                const height = Math.max(1, Math.round(width / aspect));

                canvas.width = width;
                canvas.height = height;
                paintBackdrop(context, width, height);
                context.drawImage(
                    modal.image,
                    crop.cropX, crop.cropY, crop.cropWidth, crop.cropHeight,
                    0, 0, width, height);
            } else {
                const scale = Math.min(1, maxWidth / active.naturalWidth, maxHeight / active.naturalHeight);
                const width = Math.max(1, Math.round(active.naturalWidth * scale));
                const height = Math.max(1, Math.round(active.naturalHeight * scale));

                canvas.width = width;
                canvas.height = height;
                paintBackdrop(context, width, height);
                context.drawImage(modal.image, 0, 0, width, height);
            }

            const blob = await canvasToBlob(canvas, active.output.type, 0.92);
            if (!blob || blob.size === 0) return null;

            return { file: new File([blob], buildExportName(active.file, active.output.type), { type: active.output.type }) };
        } catch (error) {
            return null;
        }
    }

    // JPEG no tiene canal alfa: sin fondo blanco, las zonas transparentes salen negras.
    function paintBackdrop(context, width, height) {
        if (active.output.type === 'image/png') return;
        context.fillStyle = '#ffffff';
        context.fillRect(0, 0, width, height);
    }

    function fitInside(aspect, maxWidth, maxHeight) {
        const boxAspect = maxWidth / maxHeight;
        return aspect >= boxAspect
            ? { width: maxWidth, height: maxWidth / aspect }
            : { width: maxHeight * aspect, height: maxHeight };
    }

    function canvasToBlob(canvas, type, quality) {
        return new Promise(resolve => {
            try {
                canvas.toBlob(resolve, type, quality);
            } catch (error) {
                resolve(null);
            }
        });
    }

    // Nombre generado: sin extensiones encadenadas ni caracteres raros del archivo original.
    function buildExportName(file, type) {
        const raw = String((file && file.name) || 'imagen');
        const base = raw.replace(/\.[^.]+$/, '').replace(/[^a-zA-Z0-9-_]+/g, '-').replace(/-+/g, '-');
        const safe = (base.replace(/^-|-$/g, '') || 'imagen').slice(0, 60);
        return safe + (type === 'image/png' ? '.png' : '.jpg');
    }

    function isHeicLike(file) {
        const name = String((file && file.name) || '').toLowerCase();
        const type = String((file && file.type) || '').toLowerCase();
        return name.endsWith('.heic') || name.endsWith('.heif') ||
            type.indexOf('heic') >= 0 || type.indexOf('heif') >= 0;
    }

    async function submitRemove(form) {
        const formData = new FormData(form);
        try {
            const response = await fetch(form.action, {
                method: 'POST',
                body: formData,
                headers: {
                    'X-Requested-With': 'XMLHttpRequest',
                    'Accept': 'application/json'
                }
            });
            const data = await response.json();
            if (!response.ok || !data.success) {
                throw new Error(data.message || 'No fue posible quitar la imagen.');
            }

            applyRemoveResult(form, data);
            showStatus(data.message || 'Imagen removida.', false);
        } catch (error) {
            showStatus(error.message || 'No fue posible quitar la imagen.', true);
        }
    }

    function applyUploadResult(form, data) {
        updateUsage(data);

        if (form.dataset.uploadKind === 'gallery') {
            appendGalleryItem(form, data);
            updateGalleryCounters(form, data);
        } else {
            updateSingletonPreview(form, data);
        }

        form.reset();
    }

    function applyRemoveResult(form, data) {
        updateUsage(data);

        const thumb = form.closest('.tppa-thumb');
        if (thumb) {
            const list = thumb.parentElement;
            thumb.remove();
            if (list && list.children.length === 0) {
                list.hidden = true;
            }
            updateGalleryCounters(form, data);
            return;
        }

        const previewArea = form.closest('[data-preview-area]');
        const slot = form.closest('[data-public-image-slot]');
        if (previewArea && slot) {
            previewArea.innerHTML = `<div class="tppa-preview ${emptyPreviewClass(slot.dataset.publicImageSlot)} tppa-preview-empty" data-empty-preview>${emptyPreviewText(slot.dataset.publicImageSlot)}</div>`;
        }
    }

    function updateSingletonPreview(form, data) {
        const slot = form.closest('[data-public-image-slot]');
        if (!slot) return;

        const previewArea = slot.querySelector('[data-preview-area]');
        if (!previewArea) return;

        const previewClass = form.dataset.previewClass || 'tppa-preview-cover';
        const previewAlt = form.dataset.previewAlt || 'Imagen actual';
        const removeAction = resolveRemoveAction(form);
        const token = form.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
        const serviceId = form.querySelector('input[name="serviceId"]')?.value || '';
        const serviceIdInput = serviceId ? `<input type="hidden" name="serviceId" value="${escapeHtml(serviceId)}" />` : '';

        previewArea.innerHTML = [
            `<img class="tppa-preview ${previewClass}" src="${escapeAttribute(data.publicUrl)}" alt="${escapeAttribute(previewAlt)}" />`,
            `<small data-size-label>${escapeHtml(data.sizeLabel || '')}</small>`,
            `<form action="${escapeAttribute(removeAction)}" method="post" data-remove-form data-remove-target="singleton">`,
            `  <input type="hidden" name="__RequestVerificationToken" value="${escapeAttribute(token)}" />`,
            serviceIdInput,
            '  <button type="submit" class="tppa-btn tppa-btn-soft">Quitar imagen</button>',
            '</form>'
        ].join('');

        const removeForm = previewArea.querySelector('[data-remove-form]');
        if (removeForm) {
            removeForm.addEventListener('submit', event => {
                event.preventDefault();
                submitRemove(removeForm);
            });
        }
    }

    function appendGalleryItem(form, data) {
        const target = form.dataset.galleryListTarget;
        const list = target ? document.querySelector(`[data-gallery-list="${cssEscape(target)}"]`) : null;
        if (!list) return;

        const removeAction = resolveRemoveAction(form);
        const token = form.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
        const alt = form.dataset.previewAlt || 'Imagen de galeria';

        const item = document.createElement('div');
        item.className = 'tppa-thumb';
        item.dataset.assetId = data.assetId || '';
        item.innerHTML = [
            `<img src="${escapeAttribute(data.publicUrl)}" alt="${escapeAttribute(alt)}" />`,
            `<small data-size-label>${escapeHtml(data.sizeLabel || '')}</small>`,
            `<form action="${escapeAttribute(removeAction)}" method="post" data-remove-form data-remove-target="gallery">`,
            `  <input type="hidden" name="__RequestVerificationToken" value="${escapeAttribute(token)}" />`,
            `  <input type="hidden" name="assetId" value="${escapeAttribute(data.assetId || '')}" />`,
            '  <button type="submit" class="tppa-link-button">Quitar</button>',
            '</form>'
        ].join('');

        const removeForm = item.querySelector('[data-remove-form]');
        removeForm.addEventListener('submit', event => {
            event.preventDefault();
            submitRemove(removeForm);
        });

        list.hidden = false;
        list.appendChild(item);
    }

    function updateUsage(data) {
        const label = document.querySelector('[data-public-image-usage-label]');
        const bar = document.querySelector('[data-public-image-usage-bar]');
        if (label && data.usageLabel) label.textContent = data.usageLabel;
        if (bar && typeof data.usagePercent === 'number') bar.style.width = `${Math.min(data.usagePercent, 100)}%`;
    }

    function updateGalleryCounters(form, data) {
        const businessCounter = document.querySelector('[data-gallery-counter]');
        if (businessCounter && typeof data.businessGalleryCount === 'number') {
            businessCounter.textContent = `${data.businessGalleryCount} de ${data.maxBusinessGalleryImages} imagenes`;
        }

        const serviceMedia = form.closest('.tppa-service-media');
        const serviceCounter = serviceMedia && serviceMedia.querySelector('[data-service-gallery-counter]');
        if (serviceCounter && typeof data.serviceGalleryCount === 'number') {
            serviceCounter.textContent = `Galeria: ${data.serviceGalleryCount} de ${data.maxServiceGalleryImages}`;
        } else if (serviceCounter) {
            const list = serviceMedia.querySelector('[data-gallery-list]');
            const max = serviceCounter.dataset.galleryMax || '6';
            serviceCounter.textContent = `Galeria: ${list ? list.children.length : 0} de ${max}`;
        }
    }

    function resolveRemoveAction(form) {
        const action = form.action || window.location.href;
        return action
            .replace(/UploadLogo$/i, 'RemoveLogo')
            .replace(/UploadCover$/i, 'RemoveCover')
            .replace(/UploadLocationImage$/i, 'RemoveLocationImage')
            .replace(/UploadBusinessGalleryImage$/i, 'RemoveBusinessGalleryImage')
            .replace(/UploadServiceMainImage$/i, 'RemoveServiceMainImage');
    }

    function closeCropper() {
        if (!modal) return;
        modal.wrapper.hidden = true;
        document.body.classList.remove('tppa-crop-open');
        if (active && active.input) active.input.value = '';
        active = null;
        revokeObjectUrl();
    }

    function revokeObjectUrl() {
        if (objectUrl) {
            URL.revokeObjectURL(objectUrl);
            objectUrl = null;
        }
    }

    function showStatus(message, isError) {
        if (!statusBox) return;
        statusBox.textContent = message;
        statusBox.classList.toggle('tppa-ajax-status-error', Boolean(isError));
        statusBox.classList.toggle('tppa-ajax-status-ok', !isError);
        statusBox.hidden = false;
    }

    function emptyPreviewClass(slot) {
        if (slot === 'logo') return 'tppa-preview-logo';
        if (slot === 'service-main') return 'tppa-preview-service-main';
        return 'tppa-preview-cover';
    }

    function emptyPreviewText(slot) {
        if (slot === 'logo') return 'Sin logo';
        if (slot === 'service-main') return 'Sin imagen';
        if (slot === 'location') return 'Sin imagen de ubicacion';
        return 'Sin portada';
    }

    function clamp(value, min, max) {
        return Math.min(Math.max(value, min), max);
    }

    function cssEscape(value) {
        if (window.CSS && CSS.escape) return CSS.escape(value);
        return String(value).replace(/"/g, '\\"');
    }

    function escapeHtml(value) {
        return String(value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function escapeAttribute(value) {
        return escapeHtml(value);
    }
})();
