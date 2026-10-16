const captureOptions = [...document.querySelectorAll('[name="CaptureMethod"]')];
if (captureOptions.length) {
  const panels = [...document.querySelectorAll('[data-capture-panel]')];
  const status = document.getElementById('capture-status');
  let selected = captureOptions.find(option => option.checked)?.value || 'Text';
  const display = () => {
    captureOptions.forEach(option => { option.checked = option.value === selected; });
    panels.forEach(panel => { panel.hidden = panel.dataset.capturePanel !== selected; });
  };
  captureOptions.forEach(option => option.addEventListener('change', () => {
    const voice = document.getElementById('voice-intake');
    let warning;
    if (voice?.dataset.recording === 'true') warning = 'Stop recording before changing the input method.';
    else if (voice?.dataset.processing === 'true') warning = 'Wait for transcription to finish before changing the input method.';
    else if (document.getElementById('Audio')?.files.length) warning = 'Transcribe or clear the audio before changing the input method. Your recording is still available.';
    else if (document.getElementById('Document')?.files.length) warning = 'Extract or clear the document before changing the input method. Your file is still selected.';
    if (warning) { status.textContent = warning; status.hidden = false; }
    else { selected = option.value; status.hidden = true; }
    display();
  }));
  document.getElementById('document-clear')?.addEventListener('click', () => {
    document.getElementById('Document').value = ''; status.hidden = true;
  });
  display();
}
