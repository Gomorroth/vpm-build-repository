# vpm-build-repository
VPMリポジトリ―を生成するアクション

## 使用方法
```yml
- uses: gomorroth/vpm-build-repository@a5c4b59e33e890fce471f6b042139ace3b5dfd7e
  with:
    source: "source.json"
    output: "vpm.json"
    repo-token: ${{ github.token }}
```
