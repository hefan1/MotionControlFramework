# Motion card API modules

Each `MotionControl.VendorApi.*` project holds one vendor's low-level P/Invoke declarations. These projects are isolated from the framework runtime and equipment flows so that adding a vendor does not spread its SDK types into the rest of the solution.

These API modules are the native SDK boundary. A driver adapter will reference one API module and `MotionControl.Framework.Contracts`, then implement the common card and axis operations. Keep vendor-specific channel numbering, unit conversion, homing parameters, status decoding, and error codes inside that adapter. The API modules intentionally do not pretend that different vendors share identical homing, interpolation, status, or error semantics.

The first common motion contracts cover card connection, logical axes, axis status, absolute and relative moves, jog, stop, servo, homing, and logical DI/DO access. A deployment may create multiple card instances of its selected adapter. Adapter selection remains explicit in the Host; equipment flows do not reference vendor APIs.

## Adapter boundary rules

- The common contract represents the small set of axis operations and status meanings used by ordinary equipment flows. It does not represent every feature exposed by a vendor SDK.
- A card adapter owns vendor API calls, engineering-unit conversion, physical channel mapping, IO polarity, homing setup, status decoding, stop behavior, and vendor error details.
- Choose an adapter around a real SDK/API family and verified behavior. Models using the same SDK may share an adapter when their common operations behave the same. Do not combine pulse and bus card drivers just because they share a vendor name.
- Keep interpolation, electronic gearing/cam, position-compare output, and controller-program features out of the mandatory axis contract. Add a small optional capability boundary only when a real equipment flow needs it.
- An unsupported operation must report that it is unsupported. It must not silently succeed, return a made-up status, or be inferred from a P/Invoke declaration.
- When replacing a card, the equipment flow should keep using logical axis/IO names and engineering units. The Host adapter selection and per-machine card, axis, and IO configuration change with the hardware.

`ZMotion/Adapter/MotionControl.Adapter.MotionCard.ZMotion` is the first concrete adapter. It is limited to the ZDevelop offline simulator at `127.0.0.1` and accepts only the reported identity `VPLC5xx-Simu`. It is not a physical-card driver. Leadshine and ADLINK currently have API declarations only.

The Leadshine folder has only low-level `LTDMC` declarations today. The reference DMC5610/DMC5410 and `PrimLESI` implementations are useful for understanding pulse-card command sequencing, but do not determine the interface or configuration of every Leadshine bus-card family. Confirm the exact card model, SDK version, and processor architecture before implementing a concrete adapter.

The repository includes managed API declarations and adapter source code, but does not copy native runtime DLLs because the reference projects contain SDK versions and processor architectures that do not consistently match. See `docs/vendor-motion-card-apis.md` before configuring a deployment.
