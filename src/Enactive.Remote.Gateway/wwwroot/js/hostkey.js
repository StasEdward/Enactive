// The JS twin of RemoteKdf.cs and HostKey.cs: every key that is not random is derived with HKDF-SHA256,
// an empty salt and a fixed info string, so one secret never serves two purposes.
import { open, seal, epochOf, EnvelopeError } from './envelope.js';
import { utf8, fromUtf8 } from './bytes.js';

export const INFO = Object.freeze({
  message: 'enactive-msg-v1',
  grantAuth: 'enactive-grant-auth-v1',
  pair: 'enactive-pair-v1'
});

export async function derive(ikmBytes, info) {
  const key = await crypto.subtle.importKey('raw', ikmBytes, 'HKDF', false, ['deriveBits']);
  const bits = await crypto.subtle.deriveBits({
    name: 'HKDF',
    hash: 'SHA-256',
    salt: new Uint8Array(0),
    info: typeof info === 'string' ? utf8(info) : info
  }, key, 256);
  return new Uint8Array(bits);
}

// One computer's key for one epoch. The secret is used for nothing but derivation: content is sealed
// with the message key, rotation grants are authenticated with the grant-auth key.
export async function hostKey(epoch, secretBytes) {
  if (secretBytes.length !== 32) throw new TypeError('A host key is 32 bytes.');
  const secret = secretBytes.slice();
  const messageKey = await derive(secret, INFO.message);
  const grantAuthKey = await derive(secret, INFO.grantAuth);
  return {
    epoch,
    secret,
    messageKey,
    grantAuthKey,
    sealText: (text, ad) => seal(messageKey, epoch, utf8(text), ad),
    async openText(sealed, ad) {
      // Refused before decrypting: the epoch is not in the associated data, so a record sealed under
      // another epoch's key would otherwise be reported as a bad tag instead of as a stale key.
      if (epochOf(sealed) !== epoch) throw new EnvelopeError('Sealed under another epoch.');
      const plaintext = await open(messageKey, sealed, ad);
      try {
        return fromUtf8(plaintext);
      } catch {
        // Authentic bytes that are not text: still an envelope that does not hold what the caller expects.
        throw new EnvelopeError('The envelope does not hold text.');
      }
    }
  };
}
