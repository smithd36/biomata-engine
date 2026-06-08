from src.config.manifest import ActionManifest
ActionManifest.load('simulation/actions.yaml').export_json('unity_sdk/Runtime/Resources/BiomataActions.json')
print("Sidecar generated successfully. Please check for the new file at `unity_sdk/Runtime/Resources/BiomataActions.json` for the new file.")