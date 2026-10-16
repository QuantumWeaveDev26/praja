const voicePanel = document.getElementById('voice-intake');
if (voicePanel) {
  const start = document.getElementById('record-start');
  const stop = document.getElementById('record-stop');
  const clear = document.getElementById('record-clear');
  const transcribe = document.getElementById('transcribe-audio');
  const state = document.getElementById('record-state');
  const status = document.getElementById('media-intake-status');
  const preview = document.getElementById('record-preview');
  const audioFile = document.getElementById('Audio');
  const form = document.getElementById('intake-form');
  let recorder, stream, chunks = [], timer, objectUrl, processing = false;
  const message = text => { status.textContent = text; status.hidden = false; };
  const release = () => { clearTimeout(timer); stream?.getTracks().forEach(track => track.stop()); stream = null; };
  const ready = () => { voicePanel.dataset.recording = 'false'; start.disabled = false; stop.disabled = true; transcribe.disabled = false; clear.disabled = !audioFile.files.length; };
  audioFile.addEventListener('change', () => { clear.disabled = !audioFile.files.length; state.textContent = audioFile.files.length ? 'Audio ready. Check citizen consent, then select Transcribe audio.' : 'Choose an audio file or record a complaint.'; });
  clear.addEventListener('click', () => {
    audioFile.value = ''; chunks = []; preview.hidden = true; preview.removeAttribute('src');
    if (objectUrl) URL.revokeObjectURL(objectUrl); objectUrl = null;
    clear.disabled = true; state.textContent = 'Audio cleared. You can record again or continue with text.'; status.hidden = true;
  });
  start.addEventListener('click', async () => {
    if (processing) return;
    if (!navigator.mediaDevices?.getUserMedia || !window.MediaRecorder) { message('Recording is not supported here. Upload WAV, MP3, WebM or Ogg audio instead.'); return; }
    start.disabled = true; transcribe.disabled = true; clear.disabled = true; voicePanel.dataset.recording = 'true';
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      const mime = ['audio/webm;codecs=opus', 'audio/ogg;codecs=opus', 'audio/webm'].find(type => MediaRecorder.isTypeSupported(type));
      recorder = mime ? new MediaRecorder(stream, { mimeType: mime }) : new MediaRecorder(stream);
      chunks = []; audioFile.value = '';
      recorder.ondataavailable = event => { if (event.data.size) chunks.push(event.data); };
      recorder.onstop = () => {
        release();
        const blob = new Blob(chunks, { type: recorder.mimeType });
        if (objectUrl) URL.revokeObjectURL(objectUrl);
        objectUrl = URL.createObjectURL(blob); preview.src = objectUrl; preview.hidden = false;
        if (!blob.size) message('No audio was captured. Record again or upload an audio file.');
        else if (!recorder.mimeType.includes('webm') && !recorder.mimeType.includes('ogg')) message('Unsupported recording format. Listen locally or upload WAV, MP3, WebM or Ogg.');
        else {
          try {
            const transfer = new DataTransfer();
            transfer.items.add(new File([blob], `citizen-recording.${recorder.mimeType.includes('ogg') ? 'ogg' : 'webm'}`, { type: blob.type }));
            audioFile.files = transfer.files;
            state.textContent = 'Recording ready. Check citizen consent, then select Transcribe audio.';
          } catch { message('Recording plays locally but could not be attached. Upload a supported audio file.'); }
        }
        ready();
      };
      recorder.onerror = () => { release(); ready(); message('Recording failed. Try recording again or upload audio.'); };
      recorder.start(); stop.disabled = false; state.textContent = 'Recording locally… maximum two minutes. Select Stop recording when finished.';
      timer = setTimeout(() => { if (recorder?.state === 'recording') recorder.stop(); }, 120000);
    } catch (error) { release(); ready(); message(error.name === 'NotAllowedError' ? 'Microphone permission was not granted. Upload audio instead.' : 'Unable to use the microphone. Upload audio instead.'); }
  });
  stop.addEventListener('click', () => { if (recorder?.state === 'recording') { stop.disabled = true; recorder.stop(); } });
  form.addEventListener('submit', async event => {
    const handler = new URL(event.submitter?.getAttribute('formaction') || form.getAttribute('action'), window.location.href).searchParams.get('handler');
    if (handler !== 'Audio' || event.defaultPrevented) return;
    event.preventDefault();
    if (processing) return;
    if (voicePanel.dataset.recording === 'true') { message('Stop recording first.'); return; }
    if (!audioFile.files.length) { message('Record and stop the audio, or choose an audio file first.'); return; }
    if (!document.getElementById('Input_Consent').checked) { message('Check citizen consent above the input choices. Your recording is still available.'); return; }
    if (voicePanel.dataset.speechConfigured !== 'true') { message('Speech transcription is not connected. Enable it in Connections. Your audio is still available.'); return; }
    const body = new FormData(form);
    for (const field of ['Document', 'Photo', 'PhotoConsent', 'Latitude', 'Longitude', 'Accuracy']) body.delete(field);
    const original = document.getElementById('Input_OriginalText');
    const wasReadOnly = original.readOnly; original.readOnly = true;
    processing = true; voicePanel.dataset.processing = 'true'; transcribe.disabled = true; start.disabled = true; clear.disabled = true; audioFile.disabled = true;
    transcribe.textContent = 'Transcribing…'; message('Transcribing audio. Keep this page open.');
    try {
      const response = await fetch(event.submitter.getAttribute('formaction'), { method: 'POST', body, headers: { Accept: 'application/json' }, credentials: 'same-origin' });
      if (response.redirected) throw new Error('Your session expired. Sign in again before processing audio.');
      if (!response.headers.get('content-type')?.includes('application/json')) throw new Error('Transcription request was rejected. Wait a minute and retry; your audio is still available.');
      const result = await response.json();
      if (!response.ok) throw new Error(result.error || 'Transcription failed. Retry; your audio is still available.');
      if (typeof result.text !== 'string' || result.text.length < 20 || result.text.length > 6000) throw new Error('No usable transcript returned. Retry or type the complaint.');
      original.value = result.text; original.dispatchEvent(new Event('input', { bubbles: true }));
      document.getElementById('Input_Source').value = 'Voice transcript';
      const receipt = document.getElementById('AnalysisToken'); if (receipt) receipt.value = '';
      document.querySelector('.intake-suggestions')?.remove();
      audioFile.value = '';
      state.textContent = voicePanel.dataset.citizen === 'true' ? 'Your words are ready. Listen or review, add the location, then submit your complaint.' : 'Transcription ready. Review the text below, then select Suggest details.';
      message(voicePanel.dataset.citizen === 'true' ? 'Listen to the transcript or ask someone you trust to check it against your recording. Correct mistakes before submitting.' : `${result.method}. Review the transcript against the audio. Then select Suggest details and Prepare draft.`);
    } catch (error) { message(error.message || 'Unable to transcribe. Retry; your audio is still available.'); }
    finally { processing = false; voicePanel.dataset.processing = 'false'; original.readOnly = wasReadOnly; audioFile.disabled = false; ready(); transcribe.textContent = 'Transcribe audio →'; }
  });
  window.addEventListener('pagehide', () => { if (recorder?.state === 'recording') recorder.stop(); release(); if (objectUrl) URL.revokeObjectURL(objectUrl); });
}
