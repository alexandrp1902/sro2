// Сквозная проверка флота M19: корпуса, пушки и модули доезжают до дока в том виде, в каком их задумали.
// Смотрит не на бой, а на витрину и на каталоги: что сервер прислал в welcome и что продаётся на месте.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket). Идёт ~10 с.
//   node tools/smoke-fleet.mjs [ws://localhost:5000/ws]

import { openSocket } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const RUN = Math.floor(100 + Math.random() * 900);

/** Девять корпусов пачки J плюс «Ослик» из M18: все десять должны быть в каталоге и в прайсе. */
const FLEET = ['starterTrader', 'needle', 'tug', 'surveyor', 'corsair', 'clipper', 'runner', 'lancer', 'dropship', 'galleon'];
const GUNS = ['shotgun', 'gauss', 'salvo'];
const MODULES = ['grapple', 'deepScanner', 'cloak', 'armorPlate'];

let failures = 0;
function check(label, ok) {
  console.log(`${ok ? 'OK  ' : 'FAIL'} ${label}`);
  if (!ok) failures++;
}

async function main() {
  const { ws, read } = openSocket(url);
  const messages = [];
  const welcome = await new Promise((resolve, reject) => {
    ws.onopen = () => ws.send(JSON.stringify({ t: 'hello', name: `Smoke-Fleet-${RUN}`, token: `smoke-fleet-${RUN}-token` }));
    ws.onerror = () => reject(new Error(`cannot connect to ${url}`));
    ws.onmessage = (e) => {
      const message = read(e.data);
      messages.push(message);
      if (message.t === 'welcome') resolve(message);
    };
    setTimeout(() => reject(new Error('timeout: welcome')), 5000);
  });
  const { hulls, weapons, modules, shop } = welcome;

  // --- каталоги ---
  const missingHulls = FLEET.filter((id) => !hulls?.[id]);
  check(`десять корпусов флота в каталоге${missingHulls.length ? `: нет ${missingHulls.join(', ')}` : ''}`, missingHulls.length === 0);

  check(`«Тягач»: захват ×${hulls?.tug?.perk?.grab}, таран ${hulls?.tug?.perk?.ram}`,
    hulls?.tug?.perk?.grab === 2 && hulls?.tug?.perk?.ram === true);
  check(`«Циркуль»: скан ${hulls?.surveyor?.perk?.scan}`, hulls?.surveyor?.perk?.scan === 4000);
  check('у «Пчелы» особенности нет', !hulls?.light?.perk);
  check(`«Корсар»: четыре слота S — ${hulls?.corsair?.weaponSlots?.join(' ')}`,
    hulls?.corsair?.weaponSlots?.length === 4);
  check(`«Галеон»: трюм ${hulls?.galleon?.cargo}`, hulls?.galleon?.cargo === 160);

  const missingGuns = GUNS.filter((id) => !weapons?.[id]);
  check(`три новые пушки в каталоге${missingGuns.length ? `: нет ${missingGuns.join(', ')}` : ''}`, missingGuns.length === 0);
  check(`дробовик: ${weapons?.shotgun?.pellets} дробин, разброс ${weapons?.shotgun?.spread}`,
    weapons?.shotgun?.pellets === 5 && weapons?.shotgun?.spread > 0);
  check(`гаусс: по щиту ×${weapons?.gauss?.shieldFactor}, по корпусу ×${weapons?.gauss?.hullFactor}`,
    weapons?.gauss?.shieldFactor < 1 && weapons?.gauss?.hullFactor > 1);
  check(`залп: ${weapons?.salvo?.salvo} ракеты, спрайт ${weapons?.salvo?.missile?.sprite}`,
    weapons?.salvo?.salvo === 4 && weapons?.salvo?.missile?.sprite === 'rocket');
  check('минного постановщика в каталоге нет — он отложен', !weapons?.mineLayer && !weapons?.mine);

  const missingModules = MODULES.filter((id) => !modules?.[id]);
  check(`четыре новых модуля в каталоге${missingModules.length ? `: нет ${missingModules.join(', ')}` : ''}`, missingModules.length === 0);
  check(`бронеплиты: прочность ×${modules?.armorPlate?.hpMul}, скорость ×${modules?.armorPlate?.speedMul}`,
    modules?.armorPlate?.hpMul === 1.15 && modules?.armorPlate?.speedMul === 0.95);
  check(`Mk3-плита крепче Mk1 и не медленнее: ×${modules?.armorPlate_mk3?.hpMul} / ×${modules?.armorPlate_mk3?.speedMul}`,
    modules?.armorPlate_mk3?.hpMul > modules?.armorPlate?.hpMul &&
    modules?.armorPlate_mk3?.speedMul === modules?.armorPlate?.speedMul);
  check(`захват ×${modules?.grapple?.grab}, сканер ${modules?.deepScanner?.scan}, маскировка ${modules?.cloak?.stealth}`,
    modules?.grapple?.grab === 1.6 && modules?.deepScanner?.scan === 4000 && modules?.cloak?.stealth === 0.4);

  // --- витрина места, где пилот стоит (Новый Порт, Ядро) ---
  const stock = shop?.stock ?? Object.keys(shop?.items ?? {});
  check(`в Новом Порту продают «Иглу» и «Тягач»: ${stock.filter((id) => id === 'needle' || id === 'tug').join(', ') || '—'}`,
    stock.includes('needle') && stock.includes('tug'));
  check('в Ядре есть дробовик и грузовой захват', stock.includes('shotgun') && stock.includes('grapple'));
  check('в Ядре нет «Галеона» и залпа — это товар Рубежа', !stock.includes('galleon') && !stock.includes('salvo'));
  check('у всех десяти корпусов есть цена', FLEET.every((id) => (shop?.hulls?.[id] ?? null) !== null));

  ws.close();
  console.log(failures === 0 ? '\nAll checks passed' : `\n${failures} check(s) failed`);
  process.exit(failures === 0 ? 0 : 1);
}

main().catch((e) => {
  console.error(e.message);
  process.exit(1);
});
