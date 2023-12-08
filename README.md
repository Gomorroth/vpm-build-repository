# vpm-build-repository
VPMリポジトリ―を生成するアクション

## 使用方法
```yml
- uses: gomorroth/vpm-build-repository@a85c79f84d33996be0e9c17e332bc0fa6d5c20de
  with:
    source: "source.json"
    output: "vpm.json"
    repo-token: ${{ github.token }}
```
