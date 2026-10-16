document.querySelectorAll('textarea[data-counter]').forEach((field) => {
  const counter = document.getElementById(field.dataset.counter);
  const update = () => { counter.textContent = `${field.value.length.toLocaleString('en-IN')} / 6,000`; };
  field.addEventListener('input', update);
  update();
});
document.querySelectorAll('form[data-busy]').forEach((form) => {
  if (form.id === 'intake-form') form.addEventListener('keydown', event => {
    if (event.key !== 'Enter' || event.isComposing || !['Input_District', 'Input_Location', 'Input_FollowUpTimeline'].includes(event.target?.id)) return;
    event.preventDefault();
    const prepare = document.getElementById('prepare-draft');
    if (!prepare || prepare.disabled || event.repeat) return;
    form.requestSubmit(prepare);
  });
  form.addEventListener('submit', (event) => {
    if (event.defaultPrevented) return;
    if (!event.submitter?.formNoValidate && !form.checkValidity()) return;
    if (form.id === 'intake-form') {
      const handler = new URL(event.submitter?.getAttribute('formaction') || form.getAttribute('action') || window.location.href, window.location.href).searchParams.get('handler');
      const warn = (message) => {
        event.preventDefault();
        const status = document.getElementById('media-intake-status');
        if (status) { status.textContent = message; status.hidden = false; }
      };
      if (document.getElementById('voice-intake')?.dataset.recording === 'true') { warn('Stop recording first, then select Transcribe audio.'); return; }
      if (document.getElementById('voice-intake')?.dataset.processing === 'true') { warn('Wait for transcription to finish before continuing.'); return; }
      if (handler === 'Audio') return; // The voice handler keeps audio available across failed requests.
      if (document.getElementById('Audio')?.files.length) { warn('Transcribe the pending audio first, or clear audio before continuing.'); return; }
      if (handler !== 'Document' && document.getElementById('Document')?.files.length) { warn('Extract the pending document first, or clear the document before continuing.'); return; }
      if (handler === 'Document' && !document.getElementById('Document')?.files.length) { warn('Choose a petition file first. For a voice recording, select Transcribe audio.'); return; }
      if (handler === 'Document' && !document.getElementById('Input_Consent')?.checked) { warn('Check citizen consent above the input choices before processing this file.'); return; }
      for (const input of Array.from(form.elements).filter(element => element instanceof HTMLInputElement && element.type === 'file')) {
        input.disabled = handler === 'Document' ? input.name !== 'Document' : handler === 'Audio' ? input.name !== 'Audio' : true;
      }
    }
    const button = event.submitter || form.querySelector('button[type="submit"], button:not([type])');
    if (button) { button.disabled = true; button.textContent = 'Saving…'; }
  });
});
