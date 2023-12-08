# vpm-build-repository
VPMリポジトリ―を生成するアクション

## 使用方法
```yml
- uses: gomorroth/vpm-build-repository@
  with:
    source: "source.json"
    output: "vpm.json"
    repo-token: ${{ github.token }}
```
