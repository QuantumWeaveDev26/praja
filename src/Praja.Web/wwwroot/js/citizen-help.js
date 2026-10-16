const citizenHelp = document.getElementById('citizen-help');
if (citizenHelp) {
  const status = document.getElementById('citizen-help-status');
  const speak = text => {
    if (!window.speechSynthesis || !window.SpeechSynthesisUtterance) { status.textContent = 'Read-aloud is unavailable in this browser. You can play your original recording or ask someone you trust for help.'; return; }
    const language = document.getElementById('Language').value === 'Kannada' ? 'kn-IN' : 'en-IN';
    const voices = window.speechSynthesis.getVoices();
    const voice = voices.find(v => v.lang.toLowerCase().startsWith(language.slice(0,2)));
    if (!voice) { status.textContent = 'No voice for the selected language is installed in this browser. Play your original recording or ask someone you trust to help review.'; return; }
    window.speechSynthesis.cancel();
    const utterance = new window.SpeechSynthesisUtterance(text); utterance.lang = language; utterance.voice = voice;
    utterance.onerror = () => { status.textContent = 'Read-aloud failed. Your complaint text is unchanged.'; };
    window.speechSynthesis.speak(utterance); status.textContent = 'Reading aloud. Use Stop reading to stop.';
  };
  document.getElementById('listen-guide').addEventListener('click', () => speak(document.getElementById('Language').value === 'Kannada' ? 'ನಿಮ್ಮ ಸಮಸ್ಯೆಯನ್ನು ಹೇಳಿ. ಧ್ವನಿ ದಾಖಲಿಸಲು ಬಟನ್ ಒತ್ತಿ. ಯಾವ ಊರು ಅಥವಾ ವಾರ್ಡ್, ಏನು ಸಮಸ್ಯೆ ಮತ್ತು ಯಾವ ಸಹಾಯ ಬೇಕು ಎಂದು ಹೇಳಿ. ನಂತರ ದಾಖಲಿಸುವುದನ್ನು ನಿಲ್ಲಿಸಿ. ನಿಮ್ಮ ಅನುಮತಿಯೊಂದಿಗೆ ಧ್ವನಿಯನ್ನು ಬರಹಕ್ಕೆ ಬದಲಾಯಿಸಿ. ಪರಿಶೀಲಿಸಿ ದೂರು ಸಲ್ಲಿಸಿ.' : 'Choose Record voice. Say what happened, where it happened and what help you need. Stop recording. Agree to speech processing and choose Turn voice into text. Listen or review the words, add your district and locality, then submit. Ask someone you trust for help if needed.'));
  document.getElementById('listen-complaint').addEventListener('click', () => {
    const text = document.getElementById('Input_OriginalText').value.trim();
    if (!text) { status.textContent = 'Record and transcribe, or type a complaint first.'; return; }
    speak(text);
  });
  document.getElementById('stop-reading').addEventListener('click', () => { window.speechSynthesis?.cancel(); status.textContent = 'Reading stopped.'; });
  window.addEventListener('pagehide', () => window.speechSynthesis?.cancel());
}
